namespace Teamscop.Api.Services.Export;

/// <summary>
/// The text served at <c>/api/v2/docs-for-llm.txt</c>.
///
/// Written for a machine reader: flat, literal, every parameter and failure mode spelled out, no
/// marketing. Kept in code rather than a file on disk so it ships with the binary and can never be
/// out of step with the routes it describes after a partial deploy.
/// </summary>
public static class ExportApiDocs
{
    public const string Text = """
    TEAMSCOP EXPORT API v2
    ======================
    Base URL : https://teamscop.com/api/v2
    Purpose  : read-only export of one company's monitoring data to a single external consumer.
    Format   : JSON (UTF-8) unless stated. Timestamps are ISO-8601 UTC unless named "business*".

    AUTHENTICATION
    --------------
    Every request except this document requires two headers:

      X-Api-Key:    tsk-...     (public identifier)
      X-Api-Secret: tss-...     (secret; treat like a password)

    There is exactly one credential pair. It is bound to one company and is READ-ONLY: it cannot
    create, modify or delete anything, and it cannot reach any other company's data.

    IP ALLOWLIST (required before any data endpoint answers)
    -------------------------------------------------------
    Data endpoints serve ONLY source IPs on this key's allowlist. Until an allowlist is set, every
    data endpoint returns 403. This is fail-closed by design: an empty allowlist never means "allow
    all". The two allowlist endpoints themselves need credentials only, not a matching IP, so a
    consumer whose address changes can always restore access.

      POST /api/v2/ip-allowlist
        body : {"ips": ["203.0.113.7", "2001:db8::1"]}
        note : REPLACES the list. 1-20 addresses. IPv4 or IPv6.
        200  : {"allowedIps":[...], "callerIp":"...", "note":"..."}

      GET /api/v2/ip-allowlist
        200  : {"allowedIps":[...], "callerIp":"..."}
        Use callerIp to discover the address to allowlist.

    ENDPOINTS
    ---------
    GET /api/v2/business-time
      The company's configured timezone and its current wall-clock time. Every "business*" field in
      this API is expressed in this zone. Read this first; it is what makes exported times
      interpretable.
      200 : {"companyId","companyName","timeZoneId","businessLocalNow","utcNow","utcOffsetHours"}

    GET /api/v2/staff
      All monitored employees.
      200 : {"staff":[{"staffUserId","username","status","statusReason","lastHeartbeatAt","createdAt"}]}
      status is one of: online | offline | broken | uninstalled | null (not yet classified).
        online      - reporting and capture healthy
        offline     - nothing reporting; machine off, asleep or disconnected
        broken      - a live reporter on the machine says the other half is broken
        uninstalled - removed with authorization, or never installed
      statusReason carries the detail, e.g. "components_missing:Teamscop.App.exe".

    GET /api/v2/team-leaders
      One row per team that has a leader.
      200 : {"leaders":[{"staffUserId","username","teamId","teamName"}]}

    GET /api/v2/teams/{teamId}/staff
      One team, its leader and its members.
      200 : {"teamId","teamName","leader":{...}|null,"members":[{"staffUserId","username","joinedAt"}]}
      404 : team not in this company.

    GET /api/v2/screenshots?staffUserId={guid}&from={iso}&to={iso}&take={1-500}
      Screenshot METADATA for a period, newest first. Image bytes are NOT inlined — each display
      carries an imageUrl to fetch separately, so a day's export stays small and only the frames you
      want cost bandwidth.
      200 : {"staffUserId","from","to","count","screenshots":[{
              "eventId","staffUserId","occurredAt","businessOccurredAt",
              "displays":[{"displayIndex","width","height","size","imageUrl"}]}]}
      Display 1 is the machine's PRIMARY monitor, matching Windows Display Settings.

    GET /api/v2/screenshots/{eventId}/image?display={n}
      The image bytes for one display. Content-Type is image/webp (older captures may be image/jpeg).
      Returns the stored bytes verbatim; there is no re-encode and no quality loss.
      404 : unknown event, wrong company, or that display was not captured.

    GET /api/v2/timetrack?staffUserId={guid}&from={iso}&to={iso}
      Worked and idle time for a period, plus the segments it was derived from. Segments are clipped
      to the requested window, so the totals always describe exactly the period you asked for.
      200 : {"staffUserId","from","to","workedSeconds","idleSeconds",
             "segments":[{"kind":"working|rest","start","end","durationSeconds"}]}

    GET /api/v2/browsing?staffUserId={guid}&from={iso}&to={iso}&take={1-500}
      Browser history for a period, newest first, de-duplicated.
      200 : {"staffUserId","from","to","visits":[{"url","domain","title","visitedAt"}]}

    PERIOD RULES
    ------------
    from and to are REQUIRED on every period endpoint and must both parse as ISO-8601.
    to must be after from. Maximum span is 31 days per request; page by calling repeatedly.

    STATUS CODES
    ------------
    200 ok
    400 malformed request (bad or missing period, invalid IP, too many addresses)
    401 missing or invalid X-Api-Key / X-Api-Secret
        "unknown key" and "wrong secret" are deliberately indistinguishable
    403 credentials valid but source IP not allowlisted, no allowlist set, or key disabled
    404 the requested company-scoped object does not exist
    429 rate limited (60 requests per minute per key)

    NOTES FOR AUTOMATED CONSUMERS
    -----------------------------
    - Poll no faster than you need. 60 req/min per key is the ceiling.
    - Screenshot images are immutable: cache them by eventId+display forever.
    - staffUserId values are stable; usernames are not - key your storage on the id.
    - An employee removed from the product keeps their historical data until retention drops it.
    - All times without a "business" prefix are UTC. Convert using /business-time.
    """;
}
