# Terrain textures

Drop image files here to texture the matching terrain material. Anything
you don't provide falls back to today's flat colour automatically — nothing
else needs to change for that to keep working.

| File        | Material                                    |
|-------------|----------------------------------------------|
| `grass.png` | Grass — the top face of any grass-covered voxel |
| `dirt.png`  | Dirt — bare dirt tops, and the dirt band exposed on dug walls |
| `rock.png`  | Rock — the bedrock band exposed on deep walls |

Notes:

- **Seamless/tileable.** Each texture repeats once per coarse tile (24
  world units) across every exposed face — top, and the walls of dug pits
  and cliffs — so a texture with visible edges will show seams.
- **Square, power-of-two.** 64x64 or 128x128 is plenty at this camera
  distance; anything bigger is wasted detail.
- **PNG.** Any bit depth Raylib can load; opaque is fine, these aren't
  blended.
- **Only these 3 files matter.** There's no separate "grass side" texture —
  the side wall under a grass column is always Dirt material (grass is
  just a thin skin on top), so `dirt.png` covers it.
- Rebuild (or just restart the game) after adding/replacing a file — they're
  loaded once at startup.
