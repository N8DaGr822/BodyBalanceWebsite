-- Run as the database administrator after reviewing a pending request with Mary.
-- Set the request reference and decision. Confirmation also requires the agreed price and exact address.
DECLARE @BookingRequestId uniqueidentifier = NULL;
DECLARE @Decision varchar(12) = 'Confirmed'; -- Confirmed or Cancelled
DECLARE @AgreedPrice decimal(10,2) = NULL;
DECLARE @ConfirmedLocation nvarchar(500) = NULL;

SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;
IF @BookingRequestId IS NULL OR @Decision IS NULL OR @Decision NOT IN ('Confirmed', 'Cancelled')
    THROW 50001, 'Set the request reference and a valid decision.', 1;

BEGIN TRY
    DECLARE @PractitionerId int = (SELECT PractitionerId FROM dbo.Appointments WHERE BookingRequestId = @BookingRequestId);
    IF @PractitionerId IS NULL
        THROW 50002, 'Request not found.', 1;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;
    -- Booking Transaction: Use the same practitioner lock as the request API.
    DECLARE @Resource nvarchar(255) = CONCAT(N'BodyBalance.Practitioner.', @PractitionerId);
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = @Resource,
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0
        THROW 50003, 'The schedule is busy. Try again later.', 1;

    DECLARE @AppointmentId bigint, @ServiceId int, @Start datetime2(0), @End datetime2(0), @Status varchar(12);
    SELECT @AppointmentId = AppointmentId, @ServiceId = ServiceId, @Start = StartAtUtc, @End = EndAtUtc, @Status = Status
    FROM dbo.Appointments WHERE BookingRequestId = @BookingRequestId AND PractitionerId = @PractitionerId;
    IF @Status IS NULL OR @Status <> 'Requested'
        THROW 50004, 'Only pending requests can be reviewed with this script.', 1;

    IF @Decision = 'Confirmed'
    BEGIN
        IF @AgreedPrice IS NULL OR @AgreedPrice < 0 OR NULLIF(LTRIM(RTRIM(@ConfirmedLocation)), N'') IS NULL
            THROW 50005, 'Confirm the price, first-visit eligibility, and exact location with Mary first.', 1;
        DECLARE @Zone nvarchar(100), @Notice int, @Interval int;
        SELECT @Zone = TimeZoneId, @Notice = MinimumNoticeHours, @Interval = SlotIntervalMinutes
        FROM dbo.Practitioners WHERE PractitionerId = @PractitionerId AND IsActive = 1;
        IF @Zone IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.PractitionerServices ps
            JOIN dbo.Services s ON s.ServiceId = ps.ServiceId
            WHERE ps.PractitionerId = @PractitionerId AND ps.ServiceId = @ServiceId
                AND ps.IsActive = 1 AND s.IsActive = 1 AND DATEADD(minute, s.DurationMinutes, @Start) = @End)
            THROW 50006, 'The practitioner or service is no longer available, or the duration changed.', 1;
        -- Approval Timing: The request must have met notice when submitted; approval may happen nearer the visit.
        IF @Start <= SYSUTCDATETIME() OR EXISTS (SELECT 1 FROM dbo.Appointments
            WHERE AppointmentId = @AppointmentId AND @Start < DATEADD(hour, @Notice, CreatedAtUtc))
            THROW 50007, 'The time is past or the request did not meet the advance-notice policy.', 1;
        DECLARE @LocalStart datetime2(0) = CONVERT(datetime2(0), @Start AT TIME ZONE 'UTC' AT TIME ZONE @Zone);
        DECLARE @LocalEnd datetime2(0) = CONVERT(datetime2(0), @End AT TIME ZONE 'UTC' AT TIME ZONE @Zone);
        DECLARE @Day tinyint = (DATEDIFF(day, '19000107', CONVERT(date, @LocalStart)) % 7 + 7) % 7;
        IF DATEDIFF(minute, CONVERT(date, @LocalStart), @LocalStart) % @Interval <> 0 OR DATEPART(second, @LocalStart) <> 0
            THROW 50008, 'The appointment no longer matches the start-time interval.', 1;
        -- Approval Hours: This conservative check requires one complete working interval to cover the visit.
        IF NOT EXISTS (SELECT 1 FROM dbo.AvailabilityRules WHERE PractitionerId = @PractitionerId
            AND DayOfWeek = @Day AND EffectiveFrom <= CONVERT(date, @LocalStart)
            AND (EffectiveThrough IS NULL OR EffectiveThrough >= CONVERT(date, @LocalStart))
            AND CONVERT(date, @LocalStart) = CONVERT(date, @LocalEnd)
            AND StartTimeLocal <= CONVERT(time, @LocalStart) AND EndTimeLocal >= CONVERT(time, @LocalEnd))
            AND NOT EXISTS (SELECT 1 FROM dbo.AvailabilityExceptions WHERE PractitionerId = @PractitionerId
                AND Kind = 'Added' AND StartAtUtc <= @Start AND EndAtUtc >= @End)
            THROW 50009, 'Current hours do not cover the visit. Review the schedule before confirming.', 1;
        IF EXISTS (SELECT 1 FROM dbo.AvailabilityExceptions WHERE PractitionerId = @PractitionerId
            AND Kind = 'Blocked' AND StartAtUtc < @End AND EndAtUtc > @Start)
            OR EXISTS (SELECT 1 FROM dbo.Appointments WHERE PractitionerId = @PractitionerId
                AND AppointmentId <> @AppointmentId AND Status IN ('Confirmed', 'Completed', 'NoShow')
                AND StartAtUtc < @End AND EndAtUtc > @Start)
            THROW 50010, 'This time is blocked or already reserved. No confirmation was saved.', 1;
    END;

    UPDATE dbo.Appointments SET Status = @Decision,
        Price = CASE WHEN @Decision = 'Confirmed' THEN @AgreedPrice ELSE Price END,
        Location = CASE WHEN @Decision = 'Confirmed' THEN @ConfirmedLocation ELSE Location END
    WHERE AppointmentId = @AppointmentId;
    COMMIT TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    SELECT BookingRequestId, Status, StartAtUtc, EndAtUtc, Price, Location
    FROM dbo.Appointments WHERE AppointmentId = @AppointmentId;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    THROW;
END CATCH;
