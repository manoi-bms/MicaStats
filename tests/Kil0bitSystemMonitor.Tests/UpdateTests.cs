using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Update;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Release metadata handling. The failure modes here are quiet rather than loud — a tag
    /// parsed wrongly offers a downgrade as an "update", and a checksum parsed loosely would
    /// wave through a file that does not match — so each rule is pinned.
    /// </summary>
    public class ReleaseParserTests
    {
        [Theory]
        [InlineData("v1.4.1", 1, 4, 1)]
        [InlineData("1.4.1", 1, 4, 1)]
        [InlineData("V2.0.0", 2, 0, 0)]
        [InlineData("v1.5", 1, 5, -1)]
        [InlineData("v1.5.0-beta.2", 1, 5, 0)]
        public void Tags_parse_into_versions(string tag, int major, int minor, int build)
        {
            var v = ReleaseParser.ParseVersion(tag);
            Assert.NotNull(v);
            Assert.Equal(major, v!.Major);
            Assert.Equal(minor, v.Minor);
            Assert.Equal(build, v.Build);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("v")]
        [InlineData("nightly")]
        public void Unparseable_tags_return_null(string? tag) =>
            Assert.Null(ReleaseParser.ParseVersion(tag));

        [Fact]
        public void Newer_versions_are_offered_and_older_ones_are_not()
        {
            var current = new Version(1, 4, 1);

            Assert.True(ReleaseParser.IsNewer(current, new Version(1, 4, 2)));
            Assert.True(ReleaseParser.IsNewer(current, new Version(1, 5, 0)));
            Assert.True(ReleaseParser.IsNewer(current, new Version(2, 0, 0)));

            Assert.False(ReleaseParser.IsNewer(current, new Version(1, 4, 1)));
            Assert.False(ReleaseParser.IsNewer(current, new Version(1, 4, 0)));
            Assert.False(ReleaseParser.IsNewer(current, new Version(0, 9, 9)));
            Assert.False(ReleaseParser.IsNewer(current, null));
        }

        [Fact]
        public void The_assembly_build_component_does_not_count_as_an_update()
        {
            // Assembly versions carry a fourth component that releases never use; ignoring it
            // stops the app from forever offering "an update" to the version already installed.
            Assert.False(ReleaseParser.IsNewer(new Version(1, 4, 1, 0), new Version(1, 4, 1)));
            Assert.False(ReleaseParser.IsNewer(new Version(1, 4, 1, 25), new Version(1, 4, 1)));
        }

        [Fact]
        public void Pre_release_tags_are_recognised()
        {
            Assert.True(ReleaseParser.IsPreReleaseTag("v1.5.0-beta.1"));
            Assert.False(ReleaseParser.IsPreReleaseTag("v1.5.0"));
        }

        private const string SampleJson = @"
        {
          ""tag_name"": ""v1.4.1"",
          ""name"": ""MicaStats v1.4.1"",
          ""body"": ""Move and resize your marks."",
          ""prerelease"": false,
          ""assets"": [
            { ""name"": ""MicaStats-v1.4.1-Setup.exe"",
              ""browser_download_url"": ""https://github.com/manoi-bms/MicaStats/releases/download/v1.4.1/MicaStats-v1.4.1-Setup.exe"",
              ""size"": 2741572 },
            { ""name"": ""MicaStats-v1.4.1-Setup.exe.sha256"",
              ""browser_download_url"": ""https://github.com/manoi-bms/MicaStats/releases/download/v1.4.1/MicaStats-v1.4.1-Setup.exe.sha256"",
              ""size"": 93 }
          ]
        }";

        [Fact]
        public void A_release_response_is_parsed()
        {
            var release = ReleaseParser.Parse(SampleJson);

            Assert.NotNull(release);
            Assert.Equal("v1.4.1", release!.TagName);
            Assert.Equal(new Version(1, 4, 1), release.Version);
            Assert.False(release.PreRelease);
            Assert.Equal(2, release.Assets.Count);
            Assert.Contains("Move and resize", release.Notes);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("[1,2,3]")]
        public void Malformed_responses_return_null_rather_than_throwing(string json) =>
            Assert.Null(ReleaseParser.Parse(json));

        [Fact]
        public void The_installer_asset_is_selected_not_the_checksum()
        {
            var release = ReleaseParser.Parse(SampleJson)!;
            var installer = ReleaseParser.FindInstaller(release.Assets);

            Assert.NotNull(installer);
            Assert.Equal("MicaStats-v1.4.1-Setup.exe", installer!.Name);
            Assert.EndsWith(".exe", installer.Name);
        }

        [Fact]
        public void The_checksum_sidecar_is_matched_to_its_installer()
        {
            var release = ReleaseParser.Parse(SampleJson)!;
            var checksum = ReleaseParser.FindChecksum(release.Assets, "MicaStats-v1.4.1-Setup.exe");

            Assert.NotNull(checksum);
            Assert.Equal("MicaStats-v1.4.1-Setup.exe.sha256", checksum!.Name);
        }

        [Fact]
        public void A_release_without_an_installer_offers_nothing_to_download()
        {
            var assets = new List<ReleaseAsset> { new("notes.txt", "https://github.com/x", 10) };
            Assert.Null(ReleaseParser.FindInstaller(assets));
            Assert.Null(ReleaseParser.FindChecksum(assets, "MicaStats-v1.4.1-Setup.exe"));
        }

        [Fact]
        public void A_sha256sum_line_yields_its_hash()
        {
            const string hash = "ef8f9d286692953f58e9e45c8af17c21d58269a901e798261175987bcefd5d05";
            Assert.Equal(hash, ReleaseParser.ParseChecksum(hash + " *MicaStats-v1.4.1-Setup.exe\n"));
            Assert.Equal(hash, ReleaseParser.ParseChecksum(hash));
            Assert.Equal(hash, ReleaseParser.ParseChecksum("  " + hash + "  MicaStats.exe  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<!DOCTYPE html><html>404</html>")]   // an error page instead of a checksum
        [InlineData("deadbeef")]                          // too short
        [InlineData("zzzz9d286692953f58e9e45c8af17c21d58269a901e798261175987bcefd5d05")]  // not hex
        public void Anything_that_is_not_a_digest_is_rejected(string? content) =>
            Assert.Null(ReleaseParser.ParseChecksum(content));

        [Fact]
        public void Hash_validation_requires_exactly_sixty_four_hex_characters()
        {
            Assert.True(ReleaseParser.IsSha256Hex(new string('a', 64)));
            Assert.True(ReleaseParser.IsSha256Hex(new string('F', 64)));
            Assert.False(ReleaseParser.IsSha256Hex(new string('a', 63)));
            Assert.False(ReleaseParser.IsSha256Hex(new string('a', 65)));
            Assert.False(ReleaseParser.IsSha256Hex(null));
        }

        [Fact]
        public void Download_sizes_are_formatted_for_display()
        {
            Assert.Equal("2.6 MB", ReleaseParser.Size(2741572));
            Assert.Equal("91 KB", ReleaseParser.Size(93000));
            Assert.Equal("0 MB", ReleaseParser.Size(0));
        }
    }

    /// <summary>Throttling and dismissal, so the checker stays quiet.</summary>
    public class UpdateNotifierTests
    {
        [Fact]
        public void A_check_is_due_when_none_has_ever_run()
        {
            Assert.True(UpdateNotifier.IsDue(new AppConfig { LastUpdateCheckUtc = "" }));
        }

        [Fact]
        public void A_check_is_not_due_again_within_the_day()
        {
            var config = new AppConfig
            {
                LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-2).ToString("o", CultureInfo.InvariantCulture),
            };
            Assert.False(UpdateNotifier.IsDue(config));
        }

        [Fact]
        public void A_check_is_due_again_after_a_day()
        {
            var config = new AppConfig
            {
                LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-25).ToString("o", CultureInfo.InvariantCulture),
            };
            Assert.True(UpdateNotifier.IsDue(config));
        }

        [Fact]
        public void An_unreadable_timestamp_forces_a_check_rather_than_blocking_one()
        {
            Assert.True(UpdateNotifier.IsDue(new AppConfig { LastUpdateCheckUtc = "not a date" }));
        }

        [Fact]
        public void A_skipped_version_is_not_announced_again()
        {
            var config = new AppConfig();
            UpdateNotifier.Skip(config, "v1.5.0");

            Assert.True(UpdateNotifier.IsSkipped(config, "v1.5.0"));
            Assert.True(UpdateNotifier.IsSkipped(config, "V1.5.0"));   // case-insensitive
            Assert.False(UpdateNotifier.IsSkipped(config, "v1.6.0"));  // a later one still is
        }

        [Fact]
        public void Nothing_is_skipped_by_default()
        {
            Assert.False(UpdateNotifier.IsSkipped(new AppConfig(), "v1.5.0"));
        }
    }
}
