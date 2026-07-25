# Terrain textures

Drop image files here to texture the matching terrain material. Anything
you don't provide falls back to today's flat colour automatically — nothing
else needs to change for that to keep working.

| Base name | Material                                    |
|-----------|----------------------------------------------|
| `grass`   | Grass — the top face of any grass-covered voxel |
| `dirt`    | Dirt — bare dirt tops, and the dirt band exposed on dug walls |
| `rock`    | Rock — the bedrock band exposed on deep walls |

Notes:

- **PNG or JPG** (`.png`, `.jpg`, `.jpeg`) — whichever you've got. No need
  to convert or export a low-res copy: anything over 256px on its longest
  side gets scaled down automatically on load, so a multi-megapixel photo
  straight off a phone is fine to drop in as-is.
- **Seamless/tileable.** Each texture repeats once per coarse tile (24
  world units) across every exposed face — top, and the walls of dug pits
  and cliffs — so a texture with visible edges will show seams. A random
  photo of grass will still *work*, it just won't tile invisibly.
- **Only these 3 materials matter.** There's no separate "grass side"
  texture — the side wall under a grass column is always Dirt material
  (grass is just a thin skin on top), so `dirt` covers it.
- Rebuild (or just restart the game) after adding/replacing a file — they're
  loaded once at startup.
