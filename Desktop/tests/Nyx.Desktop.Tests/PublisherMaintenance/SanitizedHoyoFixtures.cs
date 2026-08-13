using System.Text;

namespace Nyx.Desktop.Tests.PublisherMaintenance;

internal static class SanitizedHoyoFixtures
{
    public const string GenshinEntry = """
        {
          "game": { "id": "gopR6Cufr3", "biz": "hk4e_global" },
          "main": {
            "branch": "main",
            "tag": "6.7.0",
            "diff_tags": ["6.6.0"],
            "password": "redacted-fixture"
          },
          "pre_download": null,
          "enable_base_pkg_predownload": true,
          "packages": [{ "category": "redacted-fixture" }]
        }
        """;

    public const string HsrEntry = """
        {
          "game": { "id": "4ziysqXOQ8", "biz": "hkrpg_global" },
          "main": {
            "branch": "main",
            "tag": "4.3.0",
            "diff_tags": []
          },
          "pre_download": {
            "branch": "predownload",
            "tag": "4.4.0",
            "diff_tags": ["4.3.0"]
          },
          "enable_base_pkg_predownload": false
        }
        """;

    public const string ZzzEntry = """
        {
          "game": { "id": "U5hbdsT9W7", "biz": "nap_global" },
          "main": {
            "branch": "main",
            "tag": "2.3.0"
          },
          "pre_download": null
        }
        """;

    public static string ValidBatch => Batch(GenshinEntry, HsrEntry, ZzzEntry);

    public static string Batch(params string[] entries) => $$"""
        {
          "retcode": 0,
          "message": "ignored-fixture-message",
          "data": {
            "game_branches": [
              {{string.Join(",\n", entries)}}
            ],
            "account": "ignored-fixture-account"
          }
        }
        """;

    public static ReadOnlyMemory<byte> Utf8(string json) => Encoding.UTF8.GetBytes(json);
}
