# Endfield released-history boundary — September 27, 2026

The official [opening notice](https://endfield.gryphline.com/en-us/news/3839), published September 23, opens the two RE-Factor banners on September 24 at 12:00 server time. The public history client now exposes separate rerun semantics. This is public source evidence, not a populated account-history capture or completed RE-Factor import.

## Public source

Unauthenticated HTTP reads of the [character page](https://ef-webview.gryphline.com/page/gacha_char) and [weapon page](https://ef-webview.gryphline.com/page/gacha_weapon) both reference [commons.22dcc4.js](https://web-static.hg-cdn.com//endfield/webview/_gacha/commons.22dcc4.js). Its SHA-256 is `043ad6747fdc38492a30670b9c4b4dba383899e8c05b0860b4e72ecfb537f9bf`, 1,592,742 bytes. No downloaded code was executed, copied into product source, or authenticated. Desktop and browser control were not used.

The client maps character `rerun` to `E_CharacterGachaPoolType_Rerun`. It obtains character tabs from `/api/record/char/meta`, separates weapon pools by `poolType`, uses `poolVersion` when displaying numbered reruns, and has separate character/weapon `rerun-counts` endpoints. Weapon history can select `pool_ids`; both histories paginate with `seq_id`. These facts identify source boundaries; they do not establish complete family identifiers, instance identity, reward rows or account counts.

## Export protection

The previous exporter queried four character categories and labelled every weapon pool Arsenal. The released client makes that assumption unsafe. The changed exporter checks the additional character category using the existing bounded request path. It permits only an explicit terminal empty page for unsupported categories. Populated or continuing unsupported history stops the entire export before the writer runs.

Weapon pools carrying a new `poolType` or `poolVersion` are unqualified, including unknown/null values. An explicit empty terminal history for such a pool permits the other qualified records to export; a populated response fails. A new discriminator on a legacy history row also fails. This deliberately supports the previously accepted legacy shapes only: a newly typed ordinary pool may also require qualification before it can export. No value is guessed to mean Arsenal.

`pulls-history-unsupported` is a terminal, non-sensitive error. Unlike authentication/structural retries, it cannot fall back to an older credential and return another export. The UI explains that the history contains a banner type not yet supported and that no file was created. Existing files, imports, cloud state and the installed launcher are untouched.

Synthetic regression cases cover populated/continuing rerun character history, typed/numbered/unknown weapon pools and rows, terminal emptiness, missing or malformed continuation fields, no output/temp file, no credential fallback, and retained legacy export. All 23 focused cases and all 3,233 .NET tests passed (3,123 core/app and 110 packaging), with zero skips. App normal/x64 and Infrastructure Release builds passed with zero warnings/errors; full format verification and diff checks passed. The formatter retains its existing workspace-load warning. Hosted integration remains a separate receipt.

## Required before RE-Factor support

Still require naturally existing official records proving selected account/server, every page, stable occurrence IDs, family versus numbered instance, counted/free/reward rows, and cumulative totals. Then implement the actual native/export/import mapping and separate character/weapon rule state, and verify merge, duplicates, reload and account isolation. Do not make pulls for evidence. Endfield cloud pull sync remains disabled. This guard is not RE-Factor support or permission to retire existing sources.

Local evidence: `D:\Pengo_Nyx\AI\audits\nyx-post18-20260927\endfield-public-source-receipt.json`. Raw public assets remain in that audit directory; account responses and credentials are absent.
