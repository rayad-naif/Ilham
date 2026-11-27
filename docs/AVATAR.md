# Giving Ilham your face

## What ships

A stylised, fully animated 2D avatar (`wwwroot/avatar.js`) that blinks, breathes, follows your cursor,
lip-syncs to her speech, and shows nine moods. It needs no model file and runs offline. It's driven by two
methods: `setMood(name)` and `setAmplitude(0..1)`.

## Making her look like your reference image (a true 3D Ilham)

I can't turn a single photo into a rigged, talking 3D model automatically — no tool really does that well yet;
you get a frozen statue that can't emote or lip-sync. The approach that actually works is to build a proper
rigged avatar once, using your image as the reference:

1. Download **VRoid Studio** (free): <https://vroid.com/en/studio>.
2. Create a female character and shape the face, hair, eyes and outfit to match your reference image. VRoid is
   made for exactly this and exports a game-ready, rigged, expressive model.
3. Export as **`.vrm`**.
4. Put the file at `src/Ilham.App/wwwroot/models/ilham.vrm`.

A 3D renderer using **three.js** + **@pixiv/three-vrm** can then load that VRM and expose the same
`setMood()` / `setAmplitude()` API, so the rest of the app is unchanged. VRM models carry standard expression
presets (happy, angry, sad, surprised, relaxed) and visemes (aa, ih, ou, ee, oh) that map directly onto
Ilham's moods and lip-sync — so she'll emote and talk in 3D just like the 2D face does now.

> The 3D renderer module is a drop-in: implement a class with `setMood`/`setAmplitude` that drives the VRM's
> expression and viseme weights, and swap it for `Avatar` in `app.js`. Everything else — chat, voice, mood,
> guardian — already works through that interface.

## If you'd rather stay 2D

The 2D face is genuinely good and zero-setup. You can also restyle it: the `EXPRESSIONS` table and the colours
at the top of `avatar.js` control brows, eyes, mouth, blush and hair tone per mood — tweak those to taste.
