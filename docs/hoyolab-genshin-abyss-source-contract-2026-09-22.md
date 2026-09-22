# Genshin Spiral Abyss source contract

Observed September 22, 2026 in the user's prepared official HoYoLAB Chrome session, under fresh authorization to navigate records and inspect read-only responses. This supersedes the missing populated-role prerequisite for Spiral Abyss only. It does not close Theater, Onslaught, inventory, currency, or the wider post-1.8 goal.

## Source and completeness

The official full Abyss page issued GET requests to:

`https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/spiralAbyss`

Parameters are `role_id`, `server`, and `schedule_type` (`1` current, `2` previous). Both requests returned HTTP 200 and `retcode: 0`. Both corresponding official page views displayed completed records. The prepared role is on Europe; identifiers, headers, and raw responses remain transient and are excluded from this document and fixtures.

Each observed period returned two floors, six chambers, and twelve half-chamber teams. The source explicitly identified quick clearing through `10-3`; it did not return invented teams for skipped floors. The current/previous schedules were distinct. This is a complete capture of the two periods that this official page exposes, not lifetime history. No continuation fields were present. The implementation requests both periods and rejects a changed root contract or an unknown continuation field.

The following source field sets were checked against every returned row in both responses, together with distinct floor/chamber/team/character identities and floor-star sums:

- Period: `schedule_id`, `start_time`, `end_time`, `total_battle_times`, `total_win_times`, `max_floor`, `total_star`, `is_unlock`, `is_just_skipped_floor`, `skipped_floor`, the six ranking arrays, and `floors`.
- Rankings: `reveal_rank`, `defeat_rank`, `damage_rank`, `take_damage_rank`, `normal_skill_rank`, `energy_skill_rank`; rows contain character ID, rarity, value, and presentation icon.
- Floor: `index`, `is_unlock`, `star`, `max_star`, `settle_time`, nullable `settle_date_time`, `ley_line_disorder`, `ley_line_disorder_upper`, `ley_line_disorder_lower`, `levels`, and presentation icon.
- Chamber: `index`, `star`, `max_star`, `top_half_floor_monster`, `bottom_half_floor_monster`, and `battles`.
- Battle: `index`, `timestamp`, `settle_date_time`, and `avatars`.
- Avatar: `id`, `level`, `rarity`, and presentation icon. Enemy: `name`, `level`, and presentation icon.

Epoch values are decimal strings. Calendar objects contain year/month/day/hour/minute/second; battle calendar values are kept as server time. Floor settlement calendar values were null in these observations. Effects are arrays of source text. Icons are presentation data and are not retained in the private record; character labels use the existing public label catalog with an honest numeric fallback.

## Normalized contract and handling

The optional `genshinEndgame` role field contains `{ abyss: [current, previous] }`. The exact normalized field set is exercised by the wholly synthetic `hoyolab-genshin-abyss-v1.fixture.json` in the independent launcher and website repositories. No fixture is a redacted account export.

The existing `endgame` consent and observation slots apply specifically to Spiral Abyss in this first implementation. The UI names this scope explicitly. Other endgame modes are not implied by the slot name.

- Verify the selected own-account role through the existing publisher binding endpoint before and after capture. Both successful period requests belong to that exact role/server.
- Capture both periods atomically, within the existing timeout, response byte limits, protected store and encrypted bundle limits. Never truncate lists or save a partial period pair.
- Reject missing fields, unknown source fields, duplicate identities, invalid calendar values, contradictory star counts, unexpected redirects, login failures and changed role binding. Keep the last complete local observation on failure.
- Preserve empty, locked, and skip-only markers distinctly. These variants are covered by synthetic schema tests; only the populated periods were observed live here. A missing response is never a zero record.
- Keep Remember separate from manual and automatic cloud sync. Existing choices remain unchanged; new/migrated-bundle defaults remain gated by the feature availability flag.
- Include Abyss in merge conflicts, capability tombstones, strict deletion timestamps, and pending-deletion observation cutoffs. The optional protected `knownEndgameAt` field is absent on old states. Old readers reject a state containing an unknown field rather than silently losing its barrier.
- Preserve the v2 bundle's absent-field compatibility. Old readers cannot consume a new Abyss payload; they must retain their previous copy and report incompatibility. Receiver support precedes launcher enablement and any new immutable release.

## Evidence boundary

Official source shapes and completeness were observed live. New capture, encrypted serialization, merge/deletion, and rendering checks use synthetic fixtures. The newly built launcher has not yet captured and synced a live Abyss copy. The launcher availability flag remains false pending receiver deployment and integrated acceptance. No installed launcher or public release was replaced by preparing this implementation.

## Verification before publication

- Launcher: 3,177 .NET tests (3,067 application/core and 110 packaging), zero failures or skips; Release App and Infrastructure builds and the x64 preflight output succeeded.
- Embedded capture: all 151 browser-script cases passed, including 13 new Abyss cases. Integer-valued JSON exponent notation also matches the browser contract in the .NET validator.
- Website: all 129 HoYo sync/view/worker cases passed, including four Abyss cases. All 569 scraper tests and strict data validation passed.
- Local synthetic Chrome preview: both period disclosures and chamber/team content render correctly; the 390-pixel mobile viewport had no horizontal overflow. The preview contained no user account data.
- Deployment smoke initially rejected the stale committed launcher manifest. Both launcher feed schemas were refreshed through the existing generator using current committed inputs; exact committed artifact and final smoke gates must pass before publication.

Live source observation, deterministic implementation tests, receiver deployment, native capture and actual automatic upload remain separate evidence levels.
