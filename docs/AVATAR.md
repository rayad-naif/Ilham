# Giving Ilham your face

## What ships

A stylised, fully animated 2D avatar (`wwwroot/avatar.js`) that blinks, breathes, follows your cursor,
lip-syncs to her speech, and shows nine moods. It needs no model file and runs offline. It's driven by two
methods: `setMood(name)` and `setAmplitude(0..1)`.

## Option: use a realistic portrait (the quickest realistic look)

The drawn avatar is an illustration and can't be made photorealistic (it's vector art). For a **realistic**
face, give her a realistic image — any AI-generated or photographed portrait you have the right to use:

1. Save it as **`portrait.png`** (or `.jpg`, `.jpeg`, `.webp`) in her data folder:
   `%LocalAppData%\Ilham\portrait.png` on Windows.
2. Start the app. She now shows that realistic face. A 🎭 button (top-right of her panel) flips between the
   realistic portrait and the animated drawn face.

Trade-off to know: a portrait image is **static** — it's a real, realistic face, but it doesn't lip-sync or
change expression. The drawn face animates (blinks, talks, emotes) but is stylised. The toggle lets you use
whichever you want in the moment. For *realistic **and** animated*, you need one of the 3D routes below (or a
talking-portrait model), because that's the only way to drive a realistic face with expressions.

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
