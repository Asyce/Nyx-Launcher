# HSR full challenge periods: source qualification

Observed September 22, 2026 in the user's prepared official Europe HoYoLAB session, under explicit read-only navigation/network authorization. This advances R4; it does not close all HSR endgame, universe, peak or Currency Wars work. Raw responses and account-bearing URLs remain transient in the browser tool. No gameplay, equipment change, reward claim, credential export or account creation was performed.

## Source boundaries

The official full detail pages issue GETs under `https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/`:

| Scope | Endpoint | Parameters | Observed complete rows, current / previous |
| --- | --- | --- | --- |
| Forgotten Hall / Memory of Chaos | `challenge` | `role_id`, `server`, `schedule_type=1/2`, `need_all=true` | 12 / 12 floors; 9 / 9 quick-cleared |
| Pure Fiction | `challenge_story` | same | 4 / 4 floors; 2 / 2 quick-cleared |
| Apocalyptic Shadow | `challenge_boss` | same | 4 / 4 floors; 2 / 2 quick-cleared |

All six full responses returned HTTP 200 and retcode 0. The page's current and previous selectors show those periods. Overview calls with `need_all=false` are incomplete and must not be used as a full record. Each full response has two schedule groups, no pagination/continuation field, unique floor IDs and unique character IDs within every returned team. Counts here are observations, never hardcoded expected account counts.

All three modes retain third teams in Starward Mode (`is_tierce`, `node_3`, `extra_star_num`). Empty teams on quick-cleared floors remain empty; their absent battle history must not be manufactured. Anomaly Arbitration, Currency Wars and rogue variants expose different detail/continuation contracts and are outside this slice.

## Exact field sets and types

Common root fields: `groups` (array), `star_num`, `battle_num`, `max_floor_id`, `extra_star_num` (numbers), `max_floor` (string), `has_data` (boolean), `all_floor_detail` (array). Forgotten Hall additionally has numeric `schedule_id`, calendar `begin_time`/`end_time`, and `max_floor_detail` (null on both full observations).

Groups contain `schedule_id` (number), `begin_time`, `end_time` (calendar), `status`, `name_mi18n` (strings), and `upper_boss`, `lower_boss`, `tierce_boss`. Bosses are null for Forgotten Hall and Pure Fiction and objects `{ id, name_mi18n, icon }` for Apocalyptic Shadow. Observed statuses are `Running`, `New` and `End`; preserve source status and the requested period without inferring lifetime history.

All calendar objects have exactly `year`, `month`, `day`, `hour`, `minute` (numeric), with no seconds. Source-local calendar precision must remain explicit; do not invent UTC or seconds.

- Forgotten Hall floors: `name` (string), `round_num`, `star_num`, `maze_id`, `extra_star_num` (numbers), `is_chaos`, `is_fast`, `is_tierce` (booleans), `node_1`, `node_2` (objects), `node_3` (null/object).
- Pure Fiction floors: the same except no `is_chaos`.
- Apocalyptic Shadow floors: `name` (string), `star_num` and `extra_star_num` are **decimal strings**, `maze_id` (number), `is_fast`, `is_tierce` (booleans), `last_update_time` (calendar), and the three nodes. There is no `round_num` or `is_chaos`.
- Forgotten Hall nodes: `challenge_time` (calendar), `avatars` (array). Quick-cleared empty teams still contain a calendar; preserve it without presenting it as an invented team battle.
- Pure Fiction nodes additionally contain `score` (decimal string) and nullable `buff`. Buff fields are numeric `id` and strings `name_mi18n`, `desc_mi18n`, `icon`, **`simple_desc_mi18m`** (source spelling).
- Apocalyptic Shadow nodes additionally contain `score` (decimal string), `boss_defeated` (boolean), nullable `buff`; `challenge_time` is nullable on quick-cleared empty teams. Its buff has `id`, `name_mi18n`, `desc_mi18n`, `icon`, without the Pure Fiction simple-description field.
- All avatar rows have exactly `id`, `level`, `rarity`, `rank` (numbers), `element`, `icon` (strings), and `is_return_assist` (boolean).

Presentation icons must not be copied into the private wire. Preserve gameplay identifiers, source text, flags, precision and all returned rows. Unknown source fields or continuation semantics must fail the whole scoped capture rather than silently dropping data.

## Star-count invariant

In all six observed responses, the sum of floor `star_num` includes the extra Starward Mode star, while root `star_num` does not. The correct observed relationship is `sum(floor.stars) = root.stars + root.extraStars`, and `sum(floor.extraStars) = root.extraStars`. Requiring floor sum to equal base stars would reject valid complete records. Both base and extra counts must remain visible and distinct.

## Implementation direction and gates

Build a wholly synthetic fixture covering all three modes, both periods, quick clears, third teams, nullable AS timestamps, string score/star precision and missing/unknown fields. Capture all six responses atomically with own-role checks before and after, using the established session and abort/byte/time bounds. Integrate through the existing optional HSR role payload and Endgame consent, protected storage, encrypted sync, conflicts, deletion barriers and pending-deletion cutoffs. UI scope should name these three challenge modes, not imply all HSR endgame records.

Receiver support must precede enabling a launcher capability. New native capture/sync and actual automatic-upload acceptance are still separate pending checks. The prepared My HoYo page is locked; do not retrieve or export its recovery credential.

## Guarded implementation and validation

The six-period capture, strict domain/wire contract, protected storage, consent/deletion barriers, merge conflicts and pending-deletion cutoffs are implemented. The website validates the same synthetic fixture and shows all three modes, both periods, base and Starward stars, all supplied teams, lossless score strings, quick-clear markers and source-local minute precision. No account response appears in the fixture.

Validation: 3,190 native tests (3,080 core/app and 110 packaging), 169 capture-script tests, 134 website/worker tests; zero skipped. x64 Release App builds with zero warnings/errors. Chrome synthetic desktop/mobile preview retains all six periods and shows no horizontal overflow (2279/2279 desktop and 375/375 mobile document/client widths). Receiver build/publication gates are recorded with the release receipt. `HsrEndgameAvailable` remains false pending integrated native capture and sync acceptance. Installed version 1.8 and its accepted capabilities remain unchanged.
