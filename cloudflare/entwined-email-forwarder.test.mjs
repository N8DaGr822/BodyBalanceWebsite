import test from "node:test";
import assert from "node:assert/strict";
import { createHmac, createHash } from "node:crypto";
import worker, { signature, deliver } from "./entwined-email-forwarder.mjs";

test("signatures bind timestamp, recipient and raw content using the API format", async () => {
  const raw = new TextEncoder().encode("test mail");
  const secret = "test-secret-with-at-least-32-characters";
  const expected = createHmac("sha256", secret).update(`1700000000\nhello@example.com\n${createHash("sha256").update(raw).digest("hex")}`).digest("hex");
  assert.equal(await signature(secret, "1700000000", "hello@example.com", raw), expected);
});

test("a failed API request retains its durable copy; success removes it", async () => {
  const previous = globalThis.fetch;
  const deleted = [];
  const env = {
    INBOX_URL: "https://example.com/api/mail/incoming", INBOX_WEBHOOK_SECRET: "test-secret-with-at-least-32-characters",
    INBOX_PENDING: { get: async () => ({ arrayBuffer: async () => new ArrayBuffer(0), customMetadata: { recipient: "hello@example.com" } }), delete: async key => deleted.push(key) }
  };
  try {
    globalThis.fetch = async () => ({ ok: false, status: 503 });
    await assert.rejects(deliver(env, "pending/test"));
    assert.deepEqual(deleted, []);
    globalThis.fetch = async () => ({ ok: true, status: 204 });
    await deliver(env, "pending/test");
    assert.deepEqual(deleted, ["pending/test"]);
  } finally { globalThis.fetch = previous; }
});

test("storage failure still attempts both Gmail destinations", async () => {
  const forwarded = [];
  const message = {
    to: "hello@entwinedandunwind.com", raw: new Blob(["message"]).stream(),
    forward: async address => forwarded.push(address)
  };
  await assert.rejects(worker.email(message, { INBOX_PENDING: { put: async () => { throw new Error("unavailable"); } } }, { waitUntil() {} }));
  assert.deepEqual(forwarded, ["nathen.leslielentz@gmail.com", "sherk.mary@gmail.com"]);
});
