# 035 Put PRWatch on Tailscale and add it to the iPhone home screen

As the user, I want to reach PRWatch from my iPhone over Tailscale, so that I can follow reviews and tap Consent or Merge from anywhere.

Category: feature

## What

Port 443 of `tailscale serve` on mac-mini-von-daniel already goes to ControlRoom (8100) and 8443 to the app on 8110, so PRWatch gets port 10000. Needs #34 (RoomKit 0.1.0 brings `--TailnetPort` / `--tailnet-port`) and a PRWatch release for `--release` (`/factory release --project PRWatch`, then merge the release PR).

1. On the Mac: `tailscale serve --bg --https=10000 http://127.0.0.1:8101`
2. From `/Volumes/External/Claude/PRWatch`: `scripts/launch-agent.sh install --release --tailnet-host mac-mini-von-daniel.tailb28929.ts.net --tailnet-port 10000` (without `--release` it runs the checkout instead).
3. On the iPhone: open `https://mac-mini-von-daniel.tailb28929.ts.net:10000` in Safari, enter the token from `~/Library/Application Support/PRWatch/access-token`, then Share → Add to Home Screen.

Check: `tailscale serve status` lists :10000 → 127.0.0.1:8101, `scripts/launch-agent.sh status` shows the agent running, and the home-screen app loads the PR list.

## Why

The iPhone is where the Consent and Merge taps happen; #11 (end-to-end test) and #27 (tap area check) both need it.

## Notes

Steps worked out on 26 Nov 2026 from the current tailscale serve config and RoomKit 0.1.0's launch-agent.sh.
