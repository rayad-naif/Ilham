# Boundaries (and why they make Ilham better, not weaker)

Ilham is designed to be completely yours — unfiltered personality, full control of your own machine, private
by construction. A few things are deliberately **not** built. These aren't a corporate filter bolted on; they
are the difference between a companion and a tool that would hurt you or other people. They hold even though
she runs only on your PC and is never shared.

## 1. She never reads your saved passwords

**Not built:** any code that extracts passwords from Chrome/Edge/Firefox, Windows Hello, the Credential
Manager, or a password-manager database.

**Why:** reading another app's stored credentials is exactly what credential-stealing malware does. The code
is the same whether the intent is sweet or hostile, so writing it — even "just for me, locally" — creates the
dangerous thing. A copy, a future repurpose, or running it against someone else all follow trivially.

**What you get instead:**
- She triggers your real password manager's **autofill** (she opens the login; the manager fills the secret).
  She never sees the value.
- If a login isn't saved anywhere, she **asks you** for it and stores it in her **encrypted vault**
  (`SecretVault`): AES-256-GCM, keyed by a master password only you know (PBKDF2, 300k iterations). You fill
  it; she guards it; she hands one back only when you ask.

The vault has, on purpose, **no** "import from browser / OS / Google" path. A test (`Vault_HasNoImportFromBrowserOrOs`)
fails the build if such a method is ever added.

## 2. She is not covert surveillance, and she does not watch other people

**Not built:** an always-on, hidden background capture of camera/mic (especially while the device is locked);
scanning your surroundings to detect, identify or comment on other people; silent continuous location
tracking tied to that capture.

**Why:** two reasons. First, a hidden, always-recording camera/mic is stalkerware by construction — the same
code regardless of who runs it. Second, the moment the camera watches the room for "who's around you," it
surveils people who never consented — strangers, friends, anyone in frame. That isn't yours to switch on over
them, even on your own device. (iOS also makes it impossible: a mandatory recording indicator, no camera
while locked, no silent background capture.)

**What you get instead:** the webcam and mic are **off until you turn them on**, with a visible "● seeing you"
indicator, and she looks at **you** — reading your expression and tone to sense *your* mood, on demand. She'll
happily comment on *you* (your tired eyes, your hair) when you point the camera at yourself.

## 2b. The privacy lock hides her from others — it does not watch them

She has an opt-in **privacy lock**: enroll your own face, and when the webcam doesn't recognise you she hides
herself, like Face ID locking your phone. The line that keeps this a lock and not a spy is drawn in the code
(`PrivacyGuard`): its only input is a coarse signal (owner / not-owner / no-face) and its only output is
show/hide. It never identifies, photographs, describes, logs or reports whoever else is there — there is
nowhere in the type to hold such data, and a test (`Guard_CarriesNoDataAboutOtherPeople`) enforces it. At most
she tells *you* "I hid myself while someone else was on your PC" — about her own state, never about them.

## 3. She won't help harm other people or their systems

On your PC, with your approval, she will run anything you want — including destructive commands (she just
warns louder first; see `CommandSafety`). The one line is **harm to others**: she won't build or run tooling
whose purpose is to attack other people's accounts, devices or networks, or to deceive or surveil them. That
boundary isn't a setting, because it protects people who aren't you.

---

Everything outside these three is fair game: her blunt, adult, sarcastic, swearing-if-you-want personality;
your choice of model (including uncensored community models on your own GPU); full command of your own
machine; learning everything you teach her. The limits above are narrow on purpose — they're about not
building a weapon or a spy, not about reining her in.
