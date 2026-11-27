# Using Ilham from your phone (incl. iPhone 15 Pro Max)

Ilham lives on your PC. Your phone is a window into her — which means you get her full brain, memory and
command-powers from your phone, as long as the PC is on.

## The realistic iPhone story

Apple deliberately stops any app from being a background "Jarvis" on iOS:
- no always-on camera or microphone (there's a mandatory recording indicator, and the camera is blocked while
  the phone is locked),
- no silent background tasks,
- no controlling or shutting down the phone itself.

So on iPhone, Ilham is a **companion window**, and the system-control / health-shutdown powers stay on the PC,
where Windows allows them. That's not a limitation of this project — it's iOS, and it's the same for every app.

## Easiest: open her in the browser (works today, no cable)

1. PC and phone on the **same Wi-Fi**.
2. On the PC, find its local IP: `ipconfig` (Windows) → something like `192.168.1.42`.
3. Run Ilham (she already listens on all interfaces: `http://0.0.0.0:8787`).
4. On the iPhone, open Safari → `http://192.168.1.42:8787`.
5. Add to Home Screen for an app-like icon.

You get her face, voice, chat, memory, and you can tell her to do things on the PC from anywhere in the house.
Voice input works best in Chrome/Edge; Safari's support is partial.

## Over the USB-C cable

A cable doesn't give an app special powers on iOS, but you can tunnel the connection so it works without Wi-Fi:
- With the phone plugged in, use macOS Internet Sharing / a USB tether so the phone can reach the PC's IP, then
  open the same URL. On Windows you can bridge via the phone's personal hotspot over USB.
- For a cleaner setup, a tool like Tailscale (free) puts your phone and PC on a private network so
  `http://<pc-name>:8787` works anywhere, cable or not.

## A real iOS app (optional, later)

If you want a Home-Screen app instead of Safari, it can be wrapped as a thin SwiftUI/WebView app and installed
via Xcode with a free Apple developer account (re-signed every 7 days) or a paid one (yearly). It still points
at the PC and still can't do the OS-level things iOS forbids — it just looks like a native app.

## Android / Windows phone-style control

Android is more permissive: a foreground service with a persistent notification can keep her mic active while
the app is open, and tasker-style automation can do more. If you later want an Android build, that's a cleaner
target than iPhone for "always listening while I'm using it."
