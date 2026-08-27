# jellyfin-web fixtures

Markup copied verbatim from jellyfin-web so the DOM tests run against what Jellyfin actually
serves rather than a hand-written approximation.

- `videoosd-10.11.0.html` — `src/controllers/playback/video/index.html` at tag `v10.11.0`.
  The action-sheet structure is built in JS (`src/components/actionSheet/actionSheet.ts`), so it
  is reproduced inline in the test instead of being copied here.

**When updating for a new Jellyfin release:** replace this file from the matching tag and re-run
`npm test`. A failure here means the selectors in `client.js` need updating — which is the
expected cost of extending a client that has no plugin API.
