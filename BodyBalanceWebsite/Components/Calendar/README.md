# Weekly calendar display

`StandardWeekView` is the MIT-licensed BlazorCalendar 3.2.1 weekly view from
https://github.com/tossnet/Blazor-Calendar/tree/f5e0e22ea6f52f617af87da31cca893125245edb/BlazorCalendar.
The upstream component hard-codes `dd.MM` and `HH:mm` and exposes no format parameters.
This local copy keeps its behavior and styles, with a local class/namespace and
US month/day and AM/PM labels. The internal upstream day-name helper is replaced by
`day.DayOfWeek`, and the drop callback returns its task for Blazor to await.
Keep the included license with these files.
When upgrading BlazorCalendar, check whether upstream supports formatting parameters
so this copy can be retired. CalendarBase, models, and the monthly view still use the package.
