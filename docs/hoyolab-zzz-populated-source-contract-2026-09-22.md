# ZZZ populated official source qualification — September 22, 2026

The user authorized read-only official HoYoLAB navigation and response inspection for the prepared Europe role. This extends R6 beyond the historical empty-account audit. All authenticated responses and account-bearing URLs remain transient in the browser tool. No credential, response body, nickname or role identifier is written here or to fixtures. No game action, equipment change, reward claim or spending occurred.

## Complete owned-Agent detail observation

The official Agent detail page first obtained `GET /event/game_record_zzz/api/zzz/avatar/basic`, with a root `avatar_list` containing 39 unique owned Agents. The rendered carousel exposes exactly those 39 entries. Navigating every entry through the page generated `GET /event/game_record_zzz/api/zzz/avatar/info` with parameters `id_list[]`, `need_wiki`, `server`, `role_id`; each inspected response returned retcode 0. The observed host is `https://sg-act-public-api.hoyolab.com`; requests use `need_wiki=true` and one `id_list[]` value per detail request. Shiyu uses the same host.

All 39 unique detail IDs equal the 39 roster IDs. Shared roster/detail values match for `id`, `level`, `name_mi18n`, `full_name_mi18n`, `element_type`, `sub_element_type`, `camp_name_mi18n`, `avatar_profession`, `rarity`, `rank`, `awaken_state`. These are observed account counts, not hardcoded expected counts. The independent overview `stats.avatar_num` and rendered Agents Recruited total also equal 39. Its `avatar_list` has only nine preview rows and must not substitute for the full roster. `/avatar/basic` uses GET with `role_id`, `server`, and its data has exactly `avatar_list`. Before/after independent count, full roster and own-role validation are required for atomic capture output.

Fourteen Agents have `weapon: null`, an empty `equip` array and `equip_plan_info: null`. Eight expose an awakening system. Null/empty must remain explicit; they cannot become zero-valued fabricated gear. All equipped disc positions are unique within an Agent. All Agent property IDs and skill types are unique within their arrays. Rarity values observed are `S` and `A`; awakening states observed are `AwakenStateNotVisible` and `AwakenStateActivated`.

### Exact object field sets

Basic Agent: `id`, `level`, `name_mi18n`, `full_name_mi18n`, `element_type`, `camp_name_mi18n`, `avatar_profession`, `rarity`, `group_icon_path`, `hollow_icon_path`, `rank`, `is_chosen`, `role_square_url`, `sub_element_type`, `awaken_state`. IDs, levels, element/profession/rank are numbers; `is_chosen` is boolean; the rest are strings.

Detail response data: `avatar_list`, `equip_wiki`, `weapon_wiki`, `avatar_wiki`, `strategy_wiki`, `cultivate_index`, `cultivate_equip`, `special_skill_icon`. Wiki/icon/planner-link maps are presentation/navigation metadata, not proof of a full inventory or extra owned equipment.

All 39 detail rows have exactly: `avatar_profession`, `awaken_state`, `camp_name_mi18n`, `element_type`, `equip`, `equip_plan_info`, `full_name_mi18n`, `group_icon_path`, `hollow_icon_path`, `id`, `level`, `name_mi18n`, `properties`, `rank`, `ranks`, `rarity`, `role_square_url`, `role_vertical_painting_url`, `skill_awaken`, `skills`, `skin_list`, `sub_element_type`, `us_full_name`, `vertical_painting_color`, `weapon`.

- Disc: `all_hit` boolean; `equip_suit` object; `equipment_type`, `id`, `invalid_property_cnt`, `level` numbers; `icon`, `name`, `rarity` strings; `main_properties`, `properties` arrays.
- Gear property (disc and W-Engine): `add`, `level`, `property_id`, `system_id` numbers; `base`, `property_name` strings; `valid` boolean. Disc `properties` have four rows and `main_properties` one in these observations; future bounds must allow qualified variants rather than assuming this account's rarity/roll counts.
- Disc suit: `desc1`, `desc2`, `name` strings; `own`, `suit_id` numbers. `own` is the returned equipped-set count, not bag inventory.
- W-Engine, nullable: `icon`, `name`, `rarity`, `talent_content`, `talent_title` strings; `id`, `level`, `profession`, `star` numbers; `main_properties`, `properties` arrays. Both property arrays contain one row on each equipped observation.
- Agent stat: `add`, `base`, `final`, `property_name` strings; `property_id` number. There are 11–12 stat rows per Agent. All final strings are non-empty. Base/add are frequently empty (372 occurrences each), which means not supplied, not zero. Non-empty values are signed/unsigned decimal strings with an optional `%` suffix. Preserve strings exactly; do not parse percentages or round values.
- Skill: `awaken_state` string; `items` array; `level`, `skill_type` numbers. Each Agent has six skills, with 1–8 items per skill; skill types observed range 0–6 and levels 1–15.
- Skill item: `awaken` boolean; `text`, `title` strings.
- Rank: `desc`, `name` strings; `id`, `pos` numbers; `is_unlocked` boolean. Six rows per Agent, positions 1–6; current `rank` ranges 0–6.
- Equipment plan, nullable: `cultivate_info`, `custom_info`, `game_default` objects; `equip_rating` string; `equip_rating_score`, `type`, `valid_property_cnt` numbers; `plan_effective_property_list` array; `plan_only_special_property` boolean. Scores have fractional precision (observed up to two decimal places); all other inspected numeric fields are integers.
- `game_default` and `custom_info`: exactly `property_list` array. Each property row has `full_name`, `name` strings; `id`, `system_id` numbers; `is_select` boolean. The same row contract applies to `plan_effective_property_list`.
- `cultivate_info`: `is_delete`, `old_plan` booleans; `name`, `plan_id` strings. Treat plan text as private account content; never reuse it as fixture data.
- Skin: `is_original`, `unlocked` booleans; `skin_id` number; `rarity`, `skin_hollow_icon_path`, `skin_name`, `skin_square_url`, `skin_vertical_painting_color`, `skin_vertical_painting_url` strings. One to four rows observed, at least one original skin per Agent. The list proves availability flags, not which skin is equipped.
- Awakening: `awaken_level`, `awaken_max_level` numbers; `has_awaken_system` boolean; `skill_awaken_items` array (0–6 rows).
- Awakening level row: `awaken_level` number; `level_show_name` string; `awaken_skill_items` array (1–6 rows).
- Awakening skill row: `awaken_simple_info` string; `skill_type` number; `skill_items` array (1–4 rows).
- Awakening skill text row: `text`, `title` strings.

Effect/skill/rank descriptions contain HTML-like formatting. One rank description has trailing whitespace. They are not valid for a trim-only plain-label validator. Any included text needs a bounded explicit source-text contract and safe display; never render authenticated markup as executable HTML. Icons/URLs should remain excluded from the private wire. No current source supplies unequipped full-bag discs or W-Engines.

## Shiyu Defense v2, two available periods

The official full page explicitly says it only shows **Critical Node Fourth and Fifth Frontier data**. Current and Previous Season controls generated GET `/event/game_record_zzz/api/zzz/hadal_info_v2` with `role_id`, `server`, `schedule_type=1/2`. Both returned retcode 0, `hadal_ver: "v2"`, distinct zones and periods. This is not all historic Shiyu floors or lifetime history.

Exact data fields: `hadal_ver`, `hadal_info_v2`, `nick_name`, `icon`. Omit nickname/icon from a mode payload; binding remains at the selected-role level.

`hadal_info_v2` fields: `zone_id` number; `hadal_begin_time`, `hadal_end_time` calendar objects; `pass_fifth_floor` boolean; `brief`, `fitfh_layer_detail`, `fourth_layer_detail` objects; `begin_time`, `end_time` decimal epoch-second strings. Preserve the source typo **fitfh_layer_detail** only in source parsing. Calendar objects contain exactly numeric `year`, `month`, `day`, `hour`, `minute`, `second`; do not conflate them with HSR's five-field minute calendars.

`brief` contains numeric `cur_period_zone_layer_count`, `max_score`, `rank_percent`, `score`, and string `rating`. Observed layer count is five, matching the two fourth-frontier and three fifth-frontier team records. Fifth-frontier team score and maximum-score sums exactly match the brief totals for both periods. Rank percentage is encoded as an integer in hundredths of a percent: the previous source value divided by 100 equals the displayed 16.01%. Preserve the raw integer, convert by 100 only for the percent label, and do not infer rank direction.

`fitfh_layer_detail` contains exactly `layer_challenge_info_list`. Each row contains `avatar_list`, `buddy`, `buffer`, `challenge_time`, `layer_id`, `max_score`, `monster_pic`, `rating`, `score`. The last four gameplay fields are number/number/string/number; `monster_pic` is a presentation string. `buffer` contains string `text`, `title`.

`fourth_layer_detail` contains `buffer`, `challenge_time`, `layer_challenge_info_list`, `rating`. Its two team rows contain exactly `avatar_list`, `buddy`, `challenge_time`, `layer_id`; no score is supplied. Do not fabricate zero scores from absent fields.

All team avatar rows contain `avatar_profession`, `element_type`, `id`, `level`, `rank`, `sub_element_type` numbers; `rarity`, `role_square_url` strings. Buddy contains `id`, `level` numbers and `rarity`, `bangboo_rectangle_url` strings. These observations are populated; missing/null variants require explicit qualification before claiming support.

No continuation field is present in either observed full response. The available-history boundary comes from the page's explicit Fourth/Fifth Frontier scope and its two period selectors, not an assumption that absent older floors/history were collected.

## Acceptance boundary

The shared synthetic fixtures exercise null/empty gear and awakening/skin/stat-precision variants. Equipped builds and Shiyu use separately scoped projections. Native availability remains gated until integrated own-role capture/sync acceptance; implementation and receiver delivery alone do not enable the capability. Browser source observation does not prove installed launcher capture or automatic upload.

Observed effect markup includes `<color=#hex>`/`</color>`, `<IconMap:Icon_...>`, `<span style="color: #fff">`/`</span>` and `</Term>`. Treat it as inert bounded text. Preserve action-icon semantics if adding a display formatter; do not insert the raw source into HTML.

## Implemented preparation, not enabled collection

`HoyoLabZzzBuildCapture` and `HoyoLabZzzEndgameCapture` now provide bounded, read-only selected-role collectors with independent native validators. Synthetic source and normalized fixtures live in `contracts/hoyolab-zzz-builds-v1.fixture.json` and `contracts/hoyolab-zzz-endgame-v1.fixture.json`. No fixture contains an authenticated response or account-derived string. CI executes both embedded scripts.

The build collector checks the selected role before and after capture, compares the independent overview count to the full roster before and after, reads exactly one detail per owned Agent, validates shared roster fields, and rejects a changing or incomplete roster. The overall deadline is 120 seconds; a response is limited to 8 MiB, cumulative responses to 24 MiB, and the published result to 3 MiB. Agent count is bounded at 512. No partial result is published on failure.

The Shiyu collector requires both available, distinct periods and all qualified Fourth/Fifth Frontier records. Every challenge timestamp includes source seconds. Fifth team score and maximum-score sums must match the summary. `rankPercentHundredths` preserves the source integer; divide by 100 only for a percentage display. Fourth Frontier receives no fabricated score field. This first contract rejects unqualified empty/missing-frontier or null-Buddy variants. It cannot label these as no records or replace an existing record with guessed empty data.

The collectors are now wired to the existing WebView selected-role sessions and protected game bundles. ZZZ is a separate core sync game with its own encrypted associated data, typed payloads, merge rules, consent and deletion barriers. `ZzzManualSyncAvailable` remains false and gates new Remember/refresh/sync controls; installed native capture/sync acceptance is still required. Builds and Shiyu both require the exact saved account slot and selected role before and after the read, ongoing publisher consent, generation ownership, and a successful protected write before background sync. New build/endgame Remember choices default off.

This preparation does not prove full-bag discs/W-Engines, other ZZZ endgame modes, Gacha history, inventory, currency, achievements or native automatic upload.

Validation: 3,198 .NET tests and 223 embedded capture tests passed, with no skips. Release App, x64 App and Infrastructure builds passed without warnings or errors; full format verification and diff checks passed. The formatter reports the existing workspace-load warning. Installed native collection and upload remain unverified.

## Third-game state and deletion compatibility

Protected sync state is schema 5. Existing schema 1-3 records retain credentials and deletion intents with automatic sync off; schema 4 retains its exact HSR/Genshin switches and last-success times. ZZZ starts off with no last-success time. Reading an older state does not rewrite it. Explicit mutations promote it to the current schema. All timestamps and per-game flags remain independently validated; changing credentials resets automatic preferences as before.

Code rotation reads and validates all three old game copies, proves the replacement has no existing copies, durably records compensation, copies every available game, promotes the new credential, and conditionally deletes the old account only if all three copied revisions still match. An unreadable or newer ZZZ copy prevents unsafe retirement. Legacy two-game revision maps load with ZZZ expected absent. Server conflict parsing accepts exact legacy two-game or modern three-game maps and rejects unknown, duplicated or malformed fields. Account cleanup includes ZZZ protected data and remote chunks; single-game removal leaves unrelated games and pull history intact.

Remember opt-out clears only the relevant payload and writes its deletion barrier. A stale captured result cannot restore it; a later explicitly enabled capture can. Automatic sync remains an additional per-game preference, preserves local data and pauses after unseen cloud deletion, and records success only after validated transfer/storage. Tests use synthetic fixtures and an in-memory cloud; no private account code or token is used.

Receiver PR https://github.com/Asyce/Nyx/pull/71 is deployed by successful run https://github.com/Asyce/Nyx/actions/runs/35725078976. Publication source and live version equal d1a657f89a1bd894699a1c91ac86b4dae07f24fb, built 2026-09-22T12:11:35.956Z. Public launcher JSON equals the exact source after parsing; formatted source SHA-256 is 7db291fe9a18008d0f87d9d90a95bca990803421ddd99118862ce14ef96e226f. This delivery does not prove a native capture or automatic upload.

Integration validation: all 3,219 .NET tests (3,109 core/app and 110 packaging) and 223 capture-script tests passed without skips. Final App, x64 App and Infrastructure Release builds and format/diff checks passed. New tests cover both typed payload round trips, encryption game isolation, opt-out and stale-result barriers, schema 4 preservation, independent ZZZ scheduling, role deletion, three-game rotation, newer-copy cleanup conflicts, and exact legacy/current conflict parsing. Synthetic validation does not substitute for native account acceptance.
