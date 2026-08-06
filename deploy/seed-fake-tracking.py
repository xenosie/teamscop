#!/usr/bin/env python3
"""Seed fake timetrack / screenshot_meta / browser_history for BVNK demo staff.

Payload envelopes match TrackingCoordinator.PersistAndEnqueueAsync:
  vaultSequence, chainHash, configVersion, occurredAtUtc, businessLocal,
  businessTimeZoneId, businessClockVersion, businessSynchronized, payloadBase64

Inner payloads match TimeTrackEngine / ScreenshotEngine / ChromeHistoryWatcher.
Idempotent: deletes prior rows tagged seedTag=fake_tracking_v1 before insert.
"""
from __future__ import annotations

import base64
import hashlib
import io
import json
import subprocess
import sys
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlparse

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:  # pragma: no cover
    Image = None  # type: ignore

COMPANY_ID = "c499684a-5259-487b-bdcb-bebe4b72a134"
SEED_TAG = "fake_tracking_v2"
CONFIG_VERSION = 1
ALGORITHM = "last_input_hysteresis_v1"


def load_company_clock() -> dict:
    """Read company PHASE5 business clock (same formula as BusinessClock.At)."""
    out = run_psql(
        f"""SELECT coalesce("BusinessTimeZoneId",'UTC')
                   || '|' || coalesce("BusinessClockSynchronized"::text,'f')
                   || '|' || coalesce("BusinessClockVersion"::text,'0')
                   || '|' || coalesce("BusinessAnchorUtc"::text,'')
                   || '|' || coalesce("BusinessAnchorYear"::text,'')
                   || '|' || coalesce("BusinessAnchorMonth"::text,'')
                   || '|' || coalesce("BusinessAnchorDay"::text,'')
                   || '|' || coalesce("BusinessAnchorHour"::text,'0')
                   || '|' || coalesce("BusinessAnchorMinute"::text,'0')
                   || '|' || coalesce("BusinessAnchorSecond"::text,'0')
            FROM companies WHERE "Id" = '{COMPANY_ID}'::uuid;"""
    ).strip()
    parts = out.split("|")
    tz = parts[0] or "UTC"
    synced = parts[1] in ("t", "true", "True")
    version = int(parts[2] or "0")
    anchor_utc = None
    if parts[3]:
        anchor_utc = datetime.fromisoformat(parts[3].replace(" ", "T"))
        if anchor_utc.tzinfo is None:
            anchor_utc = anchor_utc.replace(tzinfo=timezone.utc)
    anchor_local = None
    if parts[4] and parts[5] and parts[6]:
        anchor_local = datetime(
            int(parts[4]), int(parts[5]), int(parts[6]),
            int(parts[7] or 0), int(parts[8] or 0), int(parts[9] or 0),
        )
    return {
        "tz": tz,
        "synced": synced,
        "version": version,
        "anchor_utc": anchor_utc,
        "anchor_local": anchor_local,
    }


def business_local_at(clock: dict, utc: datetime) -> datetime:
    """Mirror Engine BusinessClock.At for synchronized companies."""
    if clock["synced"] and clock["anchor_utc"] and clock["anchor_local"]:
        delta = utc - clock["anchor_utc"]
        return clock["anchor_local"] + delta
    # Unsynchronized: treat TZ as fixed offset UTC±HH:MM when possible.
    tz = clock["tz"]
    offset = parse_fixed_offset(tz)
    return (utc.astimezone(timezone.utc) + offset).replace(tzinfo=None)


def parse_fixed_offset(tz_id: str) -> timedelta:
    s = tz_id.strip().upper().replace("UTC", "").strip()
    if not s:
        return timedelta(0)
    neg = s.startswith("-")
    s = s.lstrip("+-")
    parts = s.split(":")
    try:
        hours = int(parts[0])
        minutes = int(parts[1]) if len(parts) > 1 else 0
        seconds = int(parts[2]) if len(parts) > 2 else 0
        td = timedelta(hours=hours, minutes=minutes, seconds=seconds)
        return -td if neg else td
    except (ValueError, IndexError):
        return timedelta(0)

BROWSE_POOLS = [
    [
        ("https://github.com/pulls", "Pull requests"),
        ("https://github.com/notifications", "Notifications"),
        ("https://stackoverflow.com/questions/tagged/csharp", "Newest 'csharp' Questions"),
        ("https://learn.microsoft.com/dotnet/csharp/", "C# documentation"),
        ("https://docs.docker.com/compose/", "Docker Compose overview"),
        ("https://console.cloud.google.com/", "Google Cloud Console"),
        ("https://linear.app/", "Linear"),
        ("https://chat.openai.com/", "ChatGPT"),
    ],
    [
        ("https://docs.google.com/spreadsheets/", "Google Sheets"),
        ("https://mail.google.com/", "Gmail"),
        ("https://calendar.google.com/", "Google Calendar"),
        ("https://www.notion.so/", "Notion"),
        ("https://www.figma.com/files/", "Figma"),
        ("https://app.slack.com/client", "Slack"),
        ("https://www.linkedin.com/feed/", "LinkedIn"),
        ("https://www.hubspot.com/", "HubSpot"),
    ],
    [
        ("https://www.reddit.com/r/programming/", "programming"),
        ("https://news.ycombinator.com/", "Hacker News"),
        ("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "YouTube"),
        ("https://en.wikipedia.org/wiki/Main_Page", "Wikipedia"),
        ("https://www.amazon.com/", "Amazon"),
        ("https://twitter.com/home", "X / Twitter"),
        ("https://www.nytimes.com/", "The New York Times"),
        ("https://www.bbc.com/news", "BBC News"),
    ],
]


def run_psql(sql: str) -> str:
    proc = subprocess.run(
        ["sudo", "-u", "postgres", "psql", "-d", "teamscop", "-v", "ON_ERROR_STOP=1", "-At"],
        input=sql,
        text=True,
        capture_output=True,
        check=False,
    )
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr or proc.stdout or "psql failed")
    return proc.stdout


def load_staff() -> list[tuple[str, str]]:
    out = run_psql(
        f"""SELECT "Id"::text || '|' || "Username"
            FROM users
            WHERE "CompanyId" = '{COMPANY_ID}'::uuid AND "Role" = 'Staff'
            ORDER BY "Username";"""
    )
    rows = []
    for line in out.splitlines():
        if not line.strip():
            continue
        uid, name = line.split("|", 1)
        rows.append((uid, name))
    return rows


def sql_quote(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def chain_hash(user_id: str, seq: int) -> str:
    return hashlib.sha256(f"{user_id}:{seq}:{SEED_TAG}".encode()).hexdigest()


def b64_json(obj: object) -> str:
    return base64.b64encode(
        json.dumps(obj, separators=(",", ":"), default=str).encode("utf-8")
    ).decode("ascii")


def envelope(user_id: str, seq: int, occurred: datetime, inner: object, clock: dict) -> str:
    biz_dt = business_local_at(clock, occurred)
    biz_local = biz_dt.isoformat(timespec="seconds")
    env = {
        "vaultSequence": seq,
        "chainHash": chain_hash(user_id, seq),
        "configVersion": CONFIG_VERSION,
        "occurredAtUtc": occurred.isoformat(),
        "businessLocal": biz_local,
        "businessTimeZoneId": clock["tz"],
        "businessClockVersion": clock["version"],
        "businessSynchronized": bool(clock["synced"]),
        "seedTag": SEED_TAG,
        "payloadBase64": b64_json(inner),
    }
    return json.dumps(env, separators=(",", ":"))


def stub_jpeg(staff_name: str, label: str, seed: int) -> bytes:
    """Small JPEG matching ScreenshotEngine shape (real SOI/EOI image)."""
    if Image is None:
        # Minimal JPEG SOI…EOI fallback
        data = bytearray(256)
        data[0], data[1] = 0xFF, 0xD8
        data[-2], data[-1] = 0xFF, 0xD9
        return bytes(data)

    # Realistic desktop capture size so admin zoom/fit layout can be tested.
    w, h = 1280, 720
    hue = (seed * 47) % 256
    img = Image.new("RGB", (w, h), (40 + hue % 80, 90 + (hue * 3) % 100, 160 + (hue * 5) % 80))
    draw = ImageDraw.Draw(img)
    draw.rectangle((0, 0, w, 48), fill=(37, 99, 235))
    draw.text((16, 14), "Teamscop · preview", fill=(255, 255, 255))
    draw.text((16, 120), staff_name[:28], fill=(255, 255, 255))
    draw.text((16, 180), label[:40], fill=(226, 232, 240))
    draw.text((16, 240), occurred_label(seed), fill=(203, 213, 225))
    draw.text((16, h - 40), f"{w}x{h}", fill=(148, 163, 184))
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=55, optimize=True)
    return buf.getvalue()


def occurred_label(seed: int) -> str:
    return f"capture #{seed}"


def workday_anchors(days_back: int = 1) -> list[datetime]:
    """Return UTC midnights for the last N weekdays (incl. today if weekday)."""
    now = datetime.now(timezone.utc)
    day = now.date()
    out: list[datetime] = []
    while len(out) < days_back:
        if day.weekday() < 5:
            out.append(datetime(day.year, day.month, day.day, tzinfo=timezone.utc))
        day -= timedelta(days=1)
    return list(reversed(out))


def build_timetracks(
    user_id: str, staff_idx: int, start_seq: int, clock: dict
) -> tuple[list[tuple], int]:
    """Working/rest segments using last_input_hysteresis_v1 field names."""
    events: list[tuple] = []
    seq = start_seq
    for day in workday_anchors(2):
        # Morning work 09:00–12:00, lunch rest 12:00–13:00, afternoon 13:00–17:00
        blocks = [
            (9, 0, 12, 0, 1),   # Working
            (12, 0, 13, 0, 0),  # Rest
            (13, 0, 17, 0, 1),  # Working
        ]
        for h0, m0, h1, m1, state in blocks:
            t = day + timedelta(hours=h0, minutes=m0 + (staff_idx % 5))
            end_block = day + timedelta(hours=h1, minutes=m1)
            # Flush-sized segments ~3–8 minutes (agent flushes ~60s; we coarsen for volume)
            step = 4 + (staff_idx % 4)  # minutes
            while t < end_block:
                ended = min(t + timedelta(minutes=step), end_block)
                idle = 12 if state == 1 else 240
                start_biz = business_local_at(clock, t)
                end_biz = business_local_at(clock, ended)
                inner = {
                    "State": state,
                    "IdleSeconds": idle,
                    "startedAtUtc": t.isoformat(),
                    "endedAtUtc": ended.isoformat(),
                    "startedAtBusiness": start_biz.isoformat(timespec="seconds"),
                    "endedAtBusiness": end_biz.isoformat(timespec="seconds"),
                    "durationSeconds": (ended - t).total_seconds(),
                    "businessTimeZoneId": clock["tz"],
                    "businessClockVersion": clock["version"],
                    "businessSynchronized": bool(clock["synced"]),
                    "algorithm": ALGORITHM,
                }
                payload = envelope(user_id, seq, ended, inner, clock)
                events.append((seq, "timetrack", ended, payload, end_biz))
                seq += 1
                t = ended
    return events, seq


def build_browsing(user_id: str, staff_idx: int, start_seq: int, clock: dict) -> tuple[list[tuple], int]:
    pool = BROWSE_POOLS[staff_idx % len(BROWSE_POOLS)]
    events: list[tuple] = []
    seq = start_seq
    for day_i, day in enumerate(workday_anchors(2)):
        # Two batches per day (morning / afternoon)
        for batch_i, hour in enumerate((10, 15)):
            when = day + timedelta(hours=hour, minutes=5 + staff_idx % 10)
            visits = []
            for j in range(4):
                url, title = pool[(staff_idx + day_i + batch_i + j) % len(pool)]
                # Domain is also computed server-side from Url; include for engine parity.
                parsed = urlparse(url if "://" in url else "https://" + url)
                domain = f"{parsed.scheme or 'https'}://{(parsed.hostname or '').lower()}"
                visits.append(
                    {
                        "Profile": "Default",
                        "Url": url,
                        "Title": title,
                        "Domain": domain,
                        "VisitedAt": (when + timedelta(minutes=j * 7)).isoformat(),
                        "VisitId": 10_000 + staff_idx * 100 + day_i * 10 + batch_i * 4 + j,
                    }
                )
            inner = {
                "fetchedAt": when.isoformat(),
                "count": len(visits),
                "visits": visits,
            }
            biz = business_local_at(clock, when)
            payload = envelope(user_id, seq, when, inner, clock)
            events.append((seq, "browser_history", when, payload, biz))
            seq += 1
    return events, seq


def build_screenshots(
    user_id: str, name: str, staff_idx: int, start_seq: int, clock: dict
) -> tuple[list[tuple], int]:
    events: list[tuple] = []
    seq = start_seq
    n = 0
    for day in workday_anchors(2):
        for hour in (9, 11, 14, 16):
            when = day + timedelta(hours=hour, minutes=staff_idx % 15)
            biz = business_local_at(clock, when)
            jpeg = stub_jpeg(
                name, f"{biz.strftime('%Y-%m-%d %H:%M')} {clock['tz']}", staff_idx * 20 + n
            )
            inner = {
                "configVersion": CONFIG_VERSION,
                "capturedAt": when.isoformat(),
                "displays": [
                    {
                        "DisplayIndex": 1,
                        "Width": 1280,
                        "Height": 720,
                        "QualityUsed": 55,
                        "size": len(jpeg),
                        "jpegBase64": base64.b64encode(jpeg).decode("ascii"),
                    }
                ],
            }
            payload = envelope(user_id, seq, when, inner, clock)
            events.append((seq, "screenshot_meta", when, payload, biz))
            seq += 1
            n += 1
    return events, seq


def main() -> int:
    staff = load_staff()
    if not staff:
        print("No BVNK staff found", file=sys.stderr)
        return 1
    clock = load_company_clock()
    print(
        f"Seeding tracking data for {len(staff)} staff (tag={SEED_TAG}) "
        f"tz={clock['tz']} synced={clock['synced']} v{clock['version']}…"
    )

    # Wipe prior seed for these event types (idempotent re-run)
    run_psql(
        f"""
        DELETE FROM agent_events
        WHERE "CompanyId" = '{COMPANY_ID}'::uuid
          AND "EventType" IN ('timetrack', 'screenshot_meta', 'browser_history')
          AND "PayloadJson" LIKE '%"seedTag":"{SEED_TAG}"%';
        """
    )

    sql_parts: list[str] = ["BEGIN;"]

    # Ensure tracking configs for all staff
    for uid, _ in staff:
        sql_parts.append(
            f"""
            INSERT INTO staff_tracking_configs (
              "StaffUserId", "CompanyId", "ScreenshotQuality", "ScreenshotPeriodSeconds",
              "TimeTrackEnabled", "BrowserHistoryEnabled", "ScreenshotEnabled",
              "ConfigVersion", "UpdatedAt"
            ) VALUES (
              '{uid}'::uuid, '{COMPANY_ID}'::uuid, 'Medium', 180,
              TRUE, TRUE, TRUE, {CONFIG_VERSION}, NOW()
            )
            ON CONFLICT ("StaffUserId") DO UPDATE SET
              "TimeTrackEnabled" = TRUE,
              "BrowserHistoryEnabled" = TRUE,
              "ScreenshotEnabled" = TRUE,
              "ConfigVersion" = EXCLUDED."ConfigVersion",
              "UpdatedAt" = NOW();
            """
        )

    total = 0
    for idx, (uid, name) in enumerate(staff):
        seq = 1
        batches: list[tuple] = []
        tt, seq = build_timetracks(uid, idx, seq, clock)
        br, seq = build_browsing(uid, idx, seq, clock)
        sc, seq = build_screenshots(uid, name, idx, seq, clock)
        batches.extend(tt)
        batches.extend(br)
        batches.extend(sc)
        # Stable order by vault sequence
        batches.sort(key=lambda x: x[0])

        for vault_seq, event_type, occurred, payload, biz_dt in batches:
            eid = str(uuid.uuid4())
            cid = str(uuid.uuid4())
            biz_iso = biz_dt.isoformat(timespec="seconds")
            ch = chain_hash(uid, vault_seq)
            sql_parts.append(
                f"""
                INSERT INTO agent_events (
                  "Id", "CompanyId", "UserId", "ClientEventId", "EventType",
                  "OccurredAt", "ReceivedAt", "PayloadJson",
                  "VaultSequence", "ChainHash",
                  "BusinessOccurredAt", "BusinessTimeZoneId", "BusinessClockVersion"
                ) VALUES (
                  '{eid}'::uuid, '{COMPANY_ID}'::uuid, '{uid}'::uuid, '{cid}'::uuid,
                  {sql_quote(event_type)},
                  {sql_quote(occurred.isoformat())}::timestamptz,
                  NOW(),
                  {sql_quote(payload)},
                  {vault_seq}, {sql_quote(ch)},
                  {sql_quote(biz_iso)}::timestamp,
                  {sql_quote(clock['tz'])}, {clock['version']}
                );
                """
            )
            total += 1

        last_seq = batches[-1][0] if batches else 0
        last_hash = chain_hash(uid, last_seq) if last_seq else None
        sql_parts.append(
            f"""
            INSERT INTO agent_sequence_states (
              "UserId", "LastVaultSequence", "LastChainHash", "UpdatedAt", "GapCount"
            ) VALUES (
              '{uid}'::uuid, {last_seq}, {sql_quote(last_hash) if last_hash else 'NULL'}, NOW(), 0
            )
            ON CONFLICT ("UserId") DO UPDATE SET
              "LastVaultSequence" = EXCLUDED."LastVaultSequence",
              "LastChainHash" = EXCLUDED."LastChainHash",
              "UpdatedAt" = NOW(),
              "GapCount" = 0;
            """
        )
        print(f"  {name}: {len(batches)} events (seq 1..{last_seq})")

    sql_parts.append("COMMIT;")

    # Write SQL to temp and execute (keeps stdin size manageable via file)
    out = Path("/tmp/teamscop-seed-fake-tracking.sql")
    out.write_text("\n".join(sql_parts), encoding="utf-8")
    print(f"Executing {out} ({total} events)…")
    proc = subprocess.run(
        ["sudo", "-u", "postgres", "psql", "-d", "teamscop", "-v", "ON_ERROR_STOP=1", "-f", str(out)],
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        print(proc.stderr[-4000:], file=sys.stderr)
        return 2

    summary = run_psql(
        f"""
        SELECT "EventType" || '=' || count(*)::text
        FROM agent_events
        WHERE "CompanyId" = '{COMPANY_ID}'::uuid
          AND "PayloadJson" LIKE '%"seedTag":"{SEED_TAG}"%'
        GROUP BY "EventType"
        ORDER BY "EventType";
        """
    )
    print("Done:")
    print(summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
