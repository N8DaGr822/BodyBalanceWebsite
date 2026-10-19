// Configure private R2 binding INBOX_PENDING, secret INBOX_WEBHOOK_SECRET, INBOX_URL,
// and a five-minute Cron Trigger. Existing Gmail destinations must remain verified.
const destinations = ["nathen.leslielentz@gmail.com", "sherk.mary@gmail.com"];
const encoder = new TextEncoder();
const hex = bytes => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");

export async function signature(secret, timestamp, recipient, raw) {
  const digest = hex(await crypto.subtle.digest("SHA-256", raw));
  const key = await crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key, encoder.encode(`${timestamp}\n${recipient}\n${digest}`)));
}

export async function deliver(env, key) {
  const object = await env.INBOX_PENDING.get(key);
  if (!object) return;
  const url = new URL(env.INBOX_URL);
  if (url.protocol !== "https:" || !env.INBOX_WEBHOOK_SECRET || env.INBOX_WEBHOOK_SECRET.length < 32)
    throw new Error("Inbox configuration is incomplete");
  const raw = await object.arrayBuffer();
  const recipient = object.customMetadata.recipient;
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const response = await fetch(url, {
    // Delivery Redirects: Inspect redirects as failed responses; never forward signed mail to another URL.
    method: "POST", redirect: "manual", signal: AbortSignal.timeout(20000), body: raw,
    headers: {
      "Content-Type": "message/rfc822", "X-Inbox-Timestamp": timestamp,
      "X-Inbox-Recipient": recipient,
      "X-Inbox-Signature": await signature(env.INBOX_WEBHOOK_SECRET, timestamp, recipient, raw)
    }
  });
  // Durable Delivery: Delete only after Azure confirms the SQL transaction completed.
  if (!response.ok) throw new Error(`Inbox returned HTTP ${response.status}`);
  await env.INBOX_PENDING.delete(key);
}

export default {
  async email(message, env, ctx) {
    const store = async () => {
      if (message.to.toLowerCase() !== "hello@entwinedandunwind.com") return;
      const raw = await new Response(message.raw).arrayBuffer();
      const digest = hex(await crypto.subtle.digest("SHA-256", raw));
      const key = `pending/${digest}`;
      await env.INBOX_PENDING.put(key, raw, { customMetadata: { recipient: message.to.toLowerCase() } });
      ctx.waitUntil(deliver(env, key).catch(error => console.error("Inbox delivery deferred; retained in R2 for scheduled retry.",
        /^Inbox returned HTTP \d{3}$/.test(error?.message) ? error.message : error?.name ?? "Unknown error")));
    };
    // Independent Copies: A failed inbox delivery must not prevent either Gmail forward.
    const results = await Promise.allSettled([store(), ...destinations.map(address => message.forward(address))]);
    if (results.some(result => result.status === "rejected")) {
      console.error("Email storage or Gmail forwarding failed. Inspect Email Routing activity and R2 pending objects.");
      throw new Error("One or more mail destinations failed");
    }
  },
  async scheduled(event, env, ctx) {
    const cursorObject = await env.INBOX_PENDING.get("retry-cursor");
    const cursor = cursorObject ? await cursorObject.text() : undefined;
    let page;
    try { page = await env.INBOX_PENDING.list({ prefix: "pending/", limit: 10, cursor }); }
    catch { page = await env.INBOX_PENDING.list({ prefix: "pending/", limit: 10 }); }
    // Fair Retries: Advance even when a message fails so one malformed email cannot block newer mail.
    await Promise.allSettled(page.objects.map(object => deliver(env, object.key).catch(error => {
      // Delivery Diagnostics: Log status or error type without email contents, addresses, or secrets.
      console.error("Inbox retry failed; message remains in private R2 storage.",
        /^Inbox returned HTTP \d{3}$/.test(error?.message) ? error.message : error?.name ?? "Unknown error");
    })));
    if (page.truncated) await env.INBOX_PENDING.put("retry-cursor", page.cursor);
    else await env.INBOX_PENDING.delete("retry-cursor");
  }
};
