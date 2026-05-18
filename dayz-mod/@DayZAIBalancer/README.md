# DayZ AI Balancer (server-side mod)

Collects live economy data (item counts, zombie counts, player counts) and POSTs
JSON snapshots to the DayZ Mod Manager's embedded HTTP listener.

## Install

1. Copy this folder (`@DayZAIBalancer/`) into your DayZ server root.
2. Add `@DayZAIBalancer` to your server's `-serverMod=...` (NOT `-mod=`) launch flag.
3. Create `serverProfile/AIBalancer/config.json`:

```json
{
  "ENDPOINT": "http://127.0.0.1:7823/api/ingest",
  "SECRET": "your-secret-token-here",
  "INTERVAL_MINUTES": 10,
  "ENABLED": true
}
```

4. Make sure your DayZ Mod Manager has the Listener running on the same port
   and the same Secret.

5. Pack `scripts/` into `addons/DayZAIBalancer.pbo` using
   `BankRev` / `Mikero PBOProject` / `addonbuilder`.

## Requirements

- [Community-Framework (CF)](https://steamcommunity.com/sharedfiles/filedetails/?id=1559212036) — for `RestContext`
- (Optional) Expansion-Core — provides `ExpansionHttpClient`

If neither is available, the mod falls back to a no-op sender and logs a warning.

## Build (mod folder layout)

```
@DayZAIBalancer/
  addons/
    DayZAIBalancer.pbo
  keys/
    DayZAIBalancer.bikey
  mod.cpp
  README.md
```

Source scripts live under `scripts/` and need to be packed into the PBO.
