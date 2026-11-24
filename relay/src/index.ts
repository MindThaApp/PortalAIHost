import { ROOM_ID_PATTERN, type Env } from "./protocol";

export { Room } from "./room";
export { Limiter } from "./limiter";

function json(status: number, error: string, extra?: HeadersInit): Response {
  return new Response(JSON.stringify({ error }), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store", ...extra },
  });
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/v1/health") {
      return new Response("ok", { headers: { "cache-control": "no-store" } });
    }

    const m = /^\/v1\/(host|device)\/([^/]+)$/.exec(url.pathname);
    if (!m) return json(404, "not_found");
    const roomId = m[2]!;
    if (!ROOM_ID_PATTERN.test(roomId)) return json(400, "bad_room");
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return json(426, "websocket_required");

    // One counter per source address, so one machine cannot open rooms or connections in bulk.
    const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
    const limited = await env.LIMITER.get(env.LIMITER.idFromName(ip)).fetch("https://limiter/hit");
    if (limited.status === 429) return json(429, "too_many_connections", { "retry-after": "60" });

    return env.ROOM.get(env.ROOM.idFromName(roomId)).fetch(request);
  },
} satisfies ExportedHandler<Env>;
