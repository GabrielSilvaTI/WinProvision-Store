const jsonHeaders = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-allow-headers": "content-type",
};

function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: jsonHeaders });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: jsonHeaders });
    if (url.pathname === "/health" && request.method === "GET") return json({ status: "ok" });
    if (url.pathname !== "/v1/install" && url.pathname !== "/v1/stats") return json({ error: "not_found" }, 404);

    const object = env.INSTALL_METRICS.get(env.INSTALL_METRICS.idFromName("global"));
    if (url.pathname === "/v1/stats" && request.method === "GET")
      return object.fetch("https://metrics.internal/stats");
    if (url.pathname === "/v1/install" && request.method === "POST") {
      const length = Number(request.headers.get("content-length") || 0);
      if (length > 2048) return json({ error: "payload_too_large" }, 413);
      return object.fetch("https://metrics.internal/install", request);
    }
    return json({ error: "method_not_allowed" }, 405);
  },
};

export class InstallMetrics {
  constructor(ctx) {
    this.ctx = ctx;
    this.sql = ctx.storage.sql;
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS install_events (
        event_id TEXT PRIMARY KEY,
        package_id TEXT NOT NULL,
        source TEXT NOT NULL,
        installed_at INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS install_events_date ON install_events(installed_at);
      CREATE INDEX IF NOT EXISTS install_events_package ON install_events(source, package_id, installed_at);
    `);
  }

  async fetch(request) {
    if (request.method === "GET") return this.stats();
    if (request.method !== "POST") return json({ error: "method_not_allowed" }, 405);

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "invalid_json" }, 400);
    }

    const packageId = typeof body?.packageId === "string" ? body.packageId.trim() : "";
    const source = typeof body?.source === "string" ? body.source.toLowerCase() : "";
    const eventId = typeof body?.eventId === "string" ? body.eventId : "";
    const safePackageId = packageId.length <= 128
      && [...packageId].length > 0
      && /^[\p{L}\p{N}][^\s/\\\p{Cc}]*$/u.test(packageId)
      && packageId.split(".").every((part) => part && part !== "." && part !== "..");
    if (!safePackageId
        || !["winget", "msstore"].includes(source)
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(eventId))
      return json({ error: "invalid_event" }, 400);

    const now = Date.now();
    const cutoff = now - 90 * 24 * 60 * 60 * 1000;
    this.sql.exec("DELETE FROM install_events WHERE installed_at < ?", cutoff);
    this.sql.exec(
      "INSERT OR IGNORE INTO install_events(event_id, package_id, source, installed_at) VALUES (?, ?, ?, ?)",
      eventId,
      packageId,
      source,
      now,
    );
    const inserted = this.sql.exec("SELECT changes() AS inserted").one().inserted === 1;
    return json({ accepted: true, duplicate: !inserted }, 202);
  }

  async stats() {
    const now = Date.now();
    const cutoff = now - 30 * 24 * 60 * 60 * 1000;
    const counts = this.sql.exec(
      `SELECT package_id AS id, source, COUNT(*) AS installCount30d
       FROM install_events WHERE installed_at >= ?
       GROUP BY source, package_id ORDER BY installCount30d DESC, package_id COLLATE NOCASE LIMIT 20000`,
      cutoff,
    ).toArray();
    return json({ schemaVersion: 1, generatedUtc: new Date(now).toISOString(), windowDays: 30, installs: counts });
  }
}
