// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using bOps.PluginHost;
using static bOps.Api.Tests.PluginLifecycleApiHarness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// V1.3-M6: the administrator plugin lifecycle HTTP transport (ADR-0037) over the REAL M5 backend, real signed archives and the real
/// host composition. The API decides nothing about lifecycle or trust; these tests prove it authorizes, bounds, transports
/// preconditions/idempotency and maps results — and that every refusal leaves the backend and the process exactly as they were.
/// </summary>
public sealed class PluginLifecycleEndpointsTests
{
    private const string Id = PluginArchiveFixture.PluginId;

    // ---- Authorization ---------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Mutations_AreRejected_WithZeroLifecycleEffect_AndZeroExecution()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        using var anonymous = harness.Anonymous();
        foreach (var response in await AllMutationsAsync(anonymous, harness, etag))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        harness.AssertUnchanged(before);
        Assert.Equal(etag, harness.Lifecycle.GetStatus(Id)!.ETag);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, harness.Lifecycle.GetStatus(Id)!.State);
        Assert.Equal(0, harness.Executions);
    }

    [Fact]
    public async Task NonAdministrator_Mutations_AreForbidden_WithZeroLifecycleEffect_AndZeroExecution()
    {
        using var harness = new PluginLifecycleApiHarness(NonAdministratorRoles);
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        // Every request is otherwise perfectly valid (current ETag, exact confirmation): only the role stands in the way.
        foreach (var response in await AllMutationsAsync(harness.Client, harness, etag))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        harness.AssertUnchanged(before);
        Assert.Equal(etag, harness.Lifecycle.GetStatus(Id)!.ETag);
        Assert.Equal(0, harness.Executions);

        // A non-administrator keeps read access to the catalog.
        using var read = await harness.Client.GetAsync(new Uri($"/api/plugins/{Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task ClientProvidedRoleClaims_DoNotGrantAdministratorAccess()
    {
        using var harness = new PluginLifecycleApiHarness(NonAdministratorRoles);
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        using var response = await SendAsync(harness.Client, HttpMethod.Post, $"/api/plugins/{Id}/disable", headers:
        [
            ("If-Match", etag), ("X-Role", "administrator"), ("X-Roles", "administrator"), ("X-Admin", "true"),
        ]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        harness.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("approver")]
    public async Task EachSingleNonAdministratorRole_IsForbidden_NotJustTheCombination(string role)
    {
        // Roles are independent claims with no hierarchy. Reaching the backend would answer 404 (unknown plugin); 403 proves it never did.
        using var harness = new PluginLifecycleApiHarness([role]);

        using var disable = await ActionAsync(harness.Client, "acme.unknown", "disable", "\"plv-1\"");
        using var upload = await UploadNewAsync(harness.Client, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
    }

    [Fact]
    public async Task TheAdministratorRoleAlone_IsSufficientForMutations()
    {
        using var harness = new PluginLifecycleApiHarness(["administrator"]);

        using var response = await UploadNewAsync(harness.Client, harness.Archive());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_ReachesTheBackend_ForEveryMutation()
    {
        using var harness = new PluginLifecycleApiHarness();

        using var garbage = await UploadNewAsync(harness.Client, [1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        Assert.Equal("archive_invalid", await CategoryAsync(garbage));

        using var enable = await EnableAsync(harness.Client, "acme.unknown", "\"plv-1\"", "1.0.0");
        using var disable = await ActionAsync(harness.Client, "acme.unknown", "disable", "\"plv-1\"");
        using var recover = await RecoverAsync(harness.Client, "acme.unknown", "\"plv-1\"");
        using var replace = await UploadForIdAsync(harness.Client, "acme.unknown", [1, 2, 3], "\"plv-1\"");
        foreach (var response in new[] { enable, disable, recover })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("plugin_not_found", await CategoryAsync(response));
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, replace.StatusCode);
        Assert.Equal(0, harness.Executions);
    }

    // ---- CSRF (existing architecture: explicit bearer credential only) ------------------------------------

    [Fact]
    public async Task AmbientCookieCredentials_NeverAuthenticate_AMutation()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        // The forged cross-site request a browser could send: it can attach cookies, but never the Authorization header.
        using var anonymous = harness.Anonymous();
        foreach (var path in new[] { "disable", "recover", "enable" })
        {
            using var response = await SendAsync(
                anonymous,
                HttpMethod.Post,
                $"/api/plugins/{Id}/{path}",
                () => new StringContent("{\"confirmedVersion\":\"1.0.0\",\"confirmed\":true}", Encoding.UTF8, "application/json"),
                headers: [("Cookie", ".bops.session=stolen; XSRF-TOKEN=guess; Authorization=Bearer%20test-api-key"), ("If-Match", etag)]);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task FormAndTextPlainForgeries_WithAValidCredential_AreRejectedBeforeTheBackend()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        // "Simple" cross-site content types (the ones a plain HTML form can send) never bind to a lifecycle body.
        foreach (var action in new[] { "enable", "recover" })
        {
            using var text = await SendAsync(
                harness.Client, HttpMethod.Post, $"/api/plugins/{Id}/{action}",
                () => new StringContent("{\"confirmedVersion\":\"1.0.0\",\"confirmed\":true}", Encoding.UTF8, "text/plain"), headers: [("If-Match", etag)]);
            using var form = await SendAsync(
                harness.Client, HttpMethod.Post, $"/api/plugins/{Id}/{action}",
                () => new FormUrlEncodedContent([new("confirmedVersion", "1.0.0"), new("confirmed", "true")]), headers: [("If-Match", etag)]);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);
        }

        // An upload is never a form: multipart is refused without being read or buffered.
        var multipart = MultipartBody(harness.Archive("2.0.0"));
        using var upload = await SendAsync(
            harness.Client, HttpMethod.Put, $"/api/plugins/{Id}/archive", () => new ByteArrayContent(multipart), MultipartContentType, [("If-Match", etag)]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, upload.StatusCode);
        Assert.Equal("unsupported_media_type", await CategoryAsync(upload));

        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task ExplicitBearerCredential_IsTheProof_AndProceedsToTheBackend()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();

        using var response = await EnableAsync(harness.Client, Id, etag, "1.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.Executions);
    }

    // ---- Install / replace -----------------------------------------------------------------------------

    [Fact]
    public async Task FirstInstall_CommitsInstalledDisabled_ReturnsTheNewETag_AndExecutesNothing()
    {
        using var harness = new PluginLifecycleApiHarness();

        using var response = await UploadNewAsync(harness.Client, harness.Archive());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"plv-1\"", response.Headers.ETag!.Tag);
        Assert.Equal($"/api/plugins/{Id}", response.Headers.Location!.OriginalString);
        var body = await ReadAsync(response);
        Assert.Equal(Id, body.GetProperty("pluginId").GetString());
        Assert.Equal("1.0.0", body.GetProperty("version").GetString());
        Assert.Equal("InstalledDisabled", body.GetProperty("state").GetString());
        Assert.Equal("\"plv-1\"", body.GetProperty("eTag").GetString());

        var status = harness.Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.InstalledDisabled, status.State);
        Assert.Equal(1, status.LifecycleVersion);
        Assert.False(harness.Manager.IsActivated(Id));
        Assert.False(harness.Manager.List().Single().Enabled);
        Assert.Equal(0, harness.Executions);
        Assert.Empty(harness.WorkAreaEntries("staging"));
        Assert.Empty(harness.WorkAreaEntries("uploads"));
    }

    [Fact]
    public async Task UploadedFilename_IsNeverThePluginIdentity()
    {
        using var harness = new PluginLifecycleApiHarness();
        var archive = harness.Archive();

        using var response = await SendAsync(
            harness.Client,
            HttpMethod.Post,
            "/api/plugins/archives",
            () =>
            {
                var content = new ByteArrayContent(archive);
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "evil.other-plugin.zip" };
                return content;
            },
            "application/zip",
            [("If-None-Match", "*"), ("X-File-Name", "evil.other-plugin.zip")]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Id, Assert.Single(harness.Manager.List()).Id);
    }

    [Fact]
    public async Task Install_RequiresAnExplicitPrecondition_AndNeverSubstitutesTheLatestRevision()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.InstallAsync();
        var before = harness.TakeSnapshot();

        using var createWithoutPrecondition = await UploadNewAsync(harness.Client, harness.Archive("2.0.0"), createOnly: false);
        using var replaceWithoutPrecondition = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0"), ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, createWithoutPrecondition.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, replaceWithoutPrecondition.StatusCode);
        Assert.Equal("precondition_required", await CategoryAsync(replaceWithoutPrecondition));
        harness.AssertUnchanged(before);
        Assert.Equal(etag, harness.Lifecycle.GetStatus(Id)!.ETag);
        Assert.Equal("1.0.0", harness.Lifecycle.GetStatus(Id)!.PluginVersion);
    }

    [Fact]
    public async Task Replacement_WithTheCurrentETag_Succeeds_ReturnsANewETag_AndNeverExecutesTheNewGeneration()
    {
        using var harness = new PluginLifecycleApiHarness();
        var first = await harness.InstallAsync();

        using var response = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0"), first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = response.Headers.ETag!.Tag;
        Assert.NotEqual(first, second);
        var status = harness.Lifecycle.GetStatus(Id)!;
        Assert.Equal("2.0.0", status.PluginVersion);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, status.State);
        Assert.Equal(second, status.ETag);
        Assert.Equal(0, harness.Executions);
        Assert.False(harness.Manager.IsActivated(Id));
    }

    [Fact]
    public async Task Replacement_WithAStaleETag_Fails412_WithZeroMutation()
    {
        using var harness = new PluginLifecycleApiHarness();
        var first = await harness.InstallAsync();
        using (var advance = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0"), first))
        {
            Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
        }

        var before = harness.TakeSnapshot();
        var current = harness.Lifecycle.GetStatus(Id)!.ETag;

        using var response = await UploadForIdAsync(harness.Client, Id, harness.Archive("3.0.0"), first);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("stale_lifecycle_version", await CategoryAsync(response));
        Assert.Equal(current, (await ReadAsync(response)).GetProperty("eTag").GetString());
        harness.AssertUnchanged(before);
        Assert.Equal("2.0.0", harness.Lifecycle.GetStatus(Id)!.PluginVersion);
    }

    [Fact]
    public async Task FirstInstall_Semantics_CreateOnlyOnAnExistingPlugin_AndIfMatchOnAnAbsentOne_AreBothPreconditionFailures()
    {
        using var harness = new PluginLifecycleApiHarness();

        // If-Match for a plugin that does not exist yet can never match.
        using var ifMatchOnAbsent = await UploadForIdAsync(harness.Client, Id, harness.Archive(), "\"plv-3\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, ifMatchOnAbsent.StatusCode);
        Assert.Empty(harness.Manager.List());

        // Identity-bound create succeeds without any ETag.
        using var created = await UploadForIdAsync(harness.Client, Id, harness.Archive(), ifMatch: null, createOnly: true);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // A second create-only upload of the same id is a precondition failure, not a silent replacement.
        var before = harness.TakeSnapshot();
        using var createAgain = await UploadNewAsync(harness.Client, harness.Archive("2.0.0"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, createAgain.StatusCode);
        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task MalformedPreconditions_AreRejectedAsBadRequests_BeforeTheBackend()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        foreach (var invalid in new[] { "*", "W/\"plv-1\"", "abc", "\"plv-\"", "\"plv--1\"", "\"plv-1\", \"plv-2\"", "plv-1" })
        {
            using var response = await ActionAsync(harness.Client, Id, "disable", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_precondition", await CategoryAsync(response));
        }

        // If-Match is meaningless when creating by manifest identity, and the two preconditions are mutually exclusive.
        using var ifMatchOnCreate = await UploadNewAsync(harness.Client, harness.Archive("2.0.0"), createOnly: false, ifMatch: etag);
        using var both = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0"), etag, createOnly: true);
        var next = harness.Archive("2.0.0");
        using var badIfNoneMatch = await SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(next), "application/zip", [("If-None-Match", "\"plv-1\"")]);
        Assert.Equal(HttpStatusCode.BadRequest, ifMatchOnCreate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badIfNoneMatch.StatusCode);

        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task RouteIdMustMatchTheManifestId_NotTheFilename()
    {
        using var harness = new PluginLifecycleApiHarness();
        var before = harness.TakeSnapshot();

        using var response = await UploadForIdAsync(harness.Client, "acme.other-plugin", harness.Archive(), ifMatch: null, createOnly: true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("identity_conflict", await CategoryAsync(response));
        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task BackendArchiveRejections_AreMappedDeterministically_AndInstallNothing()
    {
        using var harness = new PluginLifecycleApiHarness();
        var before = harness.TakeSnapshot();

        using var notAZip = await UploadNewAsync(harness.Client, Encoding.UTF8.GetBytes("this is not a zip archive"));
        using var unsigned = await UploadNewAsync(harness.Client, harness.Archive(sign: false));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAZip.StatusCode);
        Assert.Equal("archive_invalid", await CategoryAsync(notAZip));
        Assert.Equal("archive", (await ReadAsync(notAZip)).GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsigned.StatusCode);
        Assert.Equal("signature_invalid", await CategoryAsync(unsigned));
        Assert.Equal("signature", (await ReadAsync(unsigned)).GetProperty("stage").GetString());

        Assert.Empty(harness.Manager.List());
        Assert.Null(harness.Lifecycle.GetStatus(Id));
        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task UntrustedPublisher_IsMappedTo422_AndInstallsNothing()
    {
        using var harness = new PluginLifecycleApiHarness(trusted: false);
        var before = harness.TakeSnapshot();

        using var response = await UploadNewAsync(harness.Client, harness.Archive());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var category = await CategoryAsync(response);
        Assert.True(category is "publisher_untrusted" or "signature_invalid", category);
        Assert.Empty(harness.Manager.List());
        Assert.Null(harness.Lifecycle.GetStatus(Id));
        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task Upload_RequiresApplicationZip_AndRejectsAMissingBody()
    {
        using var harness = new PluginLifecycleApiHarness();
        var before = harness.TakeSnapshot();

        foreach (var contentType in new[] { "application/json", "application/octet-stream", "text/plain" })
        {
            using var wrong = await UploadNewAsync(harness.Client, harness.Archive(), contentType: contentType);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrong.StatusCode);
            Assert.Equal("unsupported_media_type", await CategoryAsync(wrong));
        }

        using var empty = await UploadNewAsync(harness.Client, []);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("upload_missing", await CategoryAsync(empty));

        harness.AssertUnchanged(before);
    }

    // ---- Upload limits ---------------------------------------------------------------------------------

    private static Dictionary<string, string?> SmallArchiveLimit => new() { ["Plugins:Archive:MaximumCompressedBytes"] = (64 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private const int HttpBodyLimit = 64 * 1024;

    [Fact]
    public async Task BodyAboveTheHttpLimit_IsRejected413_WhateverContentLengthClaims_WithNothingCommitted()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: SmallArchiveLimit, configureServices: new GateState().Configure);
        var before = harness.TakeSnapshot();

        // Declared length: refused early. Absent or untruthful length, on a server enforcing nothing itself: refused on the bytes actually read.
        using var declared = await UploadNewAsync(harness.Client, new byte[HttpBodyLimit + 4096]);
        using var undeclared = await SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new GeneratedStreamContent(HttpBodyLimit + 4096), "application/zip",
            [("If-None-Match", "*"), (GateState.HideLengthHeader, "true")]);
        using var understated = await SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(new byte[HttpBodyLimit + 4096]), "application/zip",
            [("If-None-Match", "*"), (GateState.HideLengthHeader, "true")]);

        foreach (var response in new[] { declared, undeclared, understated })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("request_body_too_large", await CategoryAsync(response));
            harness.AssertSanitized(await response.Content.ReadAsStringAsync());
        }

        Assert.Empty(harness.Manager.List());
        Assert.Equal(0, harness.Executions);
        Assert.Empty(harness.WorkAreaEntries("uploads"));
        Assert.Empty(harness.WorkAreaEntries("staging"));
        harness.AssertUnchanged(before);
    }

    [Fact]
    public async Task BackendArchiveLimits_AreMappedDistinctly_FromTheHttpBodyLimit()
    {
        var configuration = SmallArchiveLimit;
        configuration["Plugins:Archive:MaximumEntries"] = "1";
        using var harness = new PluginLifecycleApiHarness(configuration: configuration);

        // Within the body limit but over an archive-content limit (three entries against a maximum of one): the backend refuses it.
        using var overArchive = await UploadNewAsync(harness.Client, harness.Archive());
        using var overBody = await UploadNewAsync(harness.Client, new byte[HttpBodyLimit + 1024]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overArchive.StatusCode);
        Assert.Equal("archive_limit_exceeded", await CategoryAsync(overArchive));
        Assert.Equal("archive", (await ReadAsync(overArchive)).GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overBody.StatusCode);
        Assert.Equal("request_body_too_large", await CategoryAsync(overBody));
        Assert.Empty(harness.Manager.List());
        Assert.Equal(0, harness.Executions);
    }

    [Fact]
    public async Task ValidBoundedArchive_UnderTheLimits_ReachesTheBackend()
    {
        using var harness = new PluginLifecycleApiHarness(configuration: SmallArchiveLimit);
        var archive = harness.Archive();
        Assert.True(archive.Length < 64 * 1024, $"fixture archive is {archive.Length} bytes");

        using var response = await UploadNewAsync(harness.Client, archive);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Id, Assert.Single(harness.Manager.List()).Id);
    }

    [Fact]
    public async Task AbortedUpload_CommitsNothing_AndReleasesTheBackend()
    {
        // The in-process test server hands the app a request only once its body is complete, so the mid-upload stall is modelled where
        // the backend actually reads: the request body stream. At the gate the caller goes away (its cancellation aborts the request).
        var gate = new GateState { AbortAtGate = true };
        using var harness = new PluginLifecycleApiHarness(configureServices: gate.Configure);
        var archive = harness.Archive();
        gate.FirstPart = archive.Length / 2;
        using var cancel = new CancellationTokenSource();

        var send = SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(archive), "application/zip",
            [("If-None-Match", "*"), ("Idempotency-Key", "abort-key"), (GateState.Header, "hold")], cancel.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await gate.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(harness.Manager.List());
        Assert.Null(harness.Lifecycle.GetStatus(Id));
        Assert.Equal(0, harness.Executions);
        Assert.Empty(harness.WorkAreaEntries("uploads"));
        Assert.Empty(harness.WorkAreaEntries("staging"));
        Assert.DoesNotContain(harness.TakeSnapshot().Tree, entry => entry.StartsWith(Id, StringComparison.Ordinal));

        // The idempotency scope was released: the same key installs normally afterwards.
        using var retry = await UploadNewAsync(harness.Client, archive, key: "abort-key");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotency-Replayed"));
    }

    // ---- Trusted node/actor context -----------------------------------------------------------------------

    [Fact]
    public async Task NodeAndActor_ComeFromTrustedServerContext_NeverFromTheRequest()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();

        using var response = await SendAsync(
            harness.Client,
            HttpMethod.Post,
            $"/api/plugins/{Id}/enable",
            () => new StringContent("{\"confirmedVersion\":\"1.0.0\",\"actor\":\"mallory-body\",\"nodeId\":\"node-body\",\"actorId\":\"mallory-body\"}", Encoding.UTF8, "application/json"),
            headers:
            [
                ("If-Match", etag), ("X-Actor", "mallory-header"), ("X-Actor-Id", "mallory-header"), ("X-Node-Id", "node-header"), ("X-Forwarded-User", "mallory-header"),
            ]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = harness.AuditText();
        var enableEvents = harness.AuditEvents().Where(evt => Property(evt, "operation").GetString() == "enable").ToArray();
        Assert.NotEmpty(enableEvents);
        foreach (var evt in enableEvents)
        {
            Assert.Equal("test-user", Property(Property(evt, "actor"), "id").GetString());
            Assert.Equal("api-user", Property(Property(evt, "actor"), "kind").GetString());
            Assert.Contains("local", Property(evt, "node").GetRawText(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("mallory", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("node-header", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("node-body", audit, StringComparison.Ordinal);
    }

    // ---- Status / ETag ---------------------------------------------------------------------------------

    [Fact]
    public async Task StatusRead_ExposesLifecycleState_AndTheETag_InHeaderAndBody_WithoutInternals()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.InstallAsync();

        using var response = await harness.Client.GetAsync(new Uri($"/api/plugins/{Id}", UriKind.Relative));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.Tag);
        using var document = JsonDocument.Parse(raw);
        var entry = document.RootElement;
        Assert.Equal(etag, entry.GetProperty("lifecycleETag").GetString());
        Assert.Equal("InstalledDisabled", entry.GetProperty("lifecycleState").GetString());
        Assert.Equal("1.0.0", entry.GetProperty("version").GetString());
        Assert.False(entry.GetProperty("enabled").GetBoolean());
        Assert.False(entry.GetProperty("loaded").GetBoolean());
        Assert.True(entry.GetProperty("verified").GetBoolean());
        harness.AssertSanitized(raw);
        foreach (var internalDetail in new[] { "installPath", "generation", "journal", "digest", "fingerprint", "signature\":", "publicKey" })
        {
            Assert.DoesNotContain(internalDetail, raw, StringComparison.OrdinalIgnoreCase);
        }

        using var list = await harness.Client.GetAsync(new Uri("/api/plugins", UriKind.Relative));
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(etag, page.RootElement.GetProperty("entries")[0].GetProperty("lifecycleETag").GetString());
    }

    [Fact]
    public async Task ExistingCatalogContract_IsPreserved_AndExtendedOnlyAdditively()
    {
        using var harness = new PluginLifecycleApiHarness();
        await harness.InstallAsync();

        var entry = await harness.StatusAsync();

        foreach (var field in new[]
        {
            "id", "version", "publisher", "installedAtUtc", "enabled", "loaded", "compatible", "signaturePresent", "verified", "trust",
            "keyId", "declaredCapabilities", "dependencies", "declaredMaxRisk", "effectiveMaxRisk", "loadError",
        })
        {
            Assert.True(entry.TryGetProperty(field, out _), $"existing catalog field '{field}' is missing");
        }

        Assert.Equal(JsonValueKind.Number, entry.GetProperty("trust").ValueKind);
    }

    [Fact]
    public async Task Status_OfAnUnknownPlugin_Is404_AndThereIsNoDeleteOperation()
    {
        using var harness = new PluginLifecycleApiHarness();
        await harness.InstallAsync();

        using var unknown = await harness.Client.GetAsync(new Uri("/api/plugins/acme.unknown", UriKind.Relative));
        using var delete = await harness.Client.DeleteAsync(new Uri($"/api/plugins/{Id}", UriKind.Relative));
        using var deleteArchive = await harness.Client.DeleteAsync(new Uri($"/api/plugins/{Id}/archive", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.False(delete.IsSuccessStatusCode);
        Assert.False(deleteArchive.IsSuccessStatusCode);
        Assert.Equal(Id, Assert.Single(harness.Manager.List()).Id);
    }

    // ---- Enable ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Enable_WithoutTheExactVersionConfirmation_ExecutesNothing_AndChangesNothing()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.InstallAsync();
        var before = harness.TakeSnapshot();

        var attempts = new List<HttpResponseMessage>
        {
            await ActionAsync(harness.Client, Id, "enable", etag),                             // no body at all
            await ActionAsync(harness.Client, Id, "enable", etag, new { }),                    // empty object
            await EnableAsync(harness.Client, Id, etag, null),                                  // explicit null
            await EnableAsync(harness.Client, Id, etag, "9.9.9"),                               // wrong version
            await EnableAsync(harness.Client, Id, etag, "1.0"),                                 // prefix of the version
            await ActionAsync(harness.Client, Id, "enable", etag, new { confirmed = true }),  // a bare boolean is not confirmation
        };
        foreach (var response in attempts)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("activation_confirmation_required", await CategoryAsync(response));
            }
        }

        harness.AssertUnchanged(before);
        Assert.Equal(0, harness.Executions);
        Assert.Equal(etag, harness.Lifecycle.GetStatus(Id)!.ETag);
        Assert.Equal(PluginLifecycleState.InstalledDisabled, harness.Lifecycle.GetStatus(Id)!.State);
    }

    [Fact]
    public async Task Enable_WithTheExactVersion_Succeeds_ReturnsANewETag_AndIsIdempotentWhenAlreadyEnabled()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();

        using var enabled = await EnableAsync(harness.Client, Id, installed, "1.0.0");

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var enabledETag = enabled.Headers.ETag!.Tag;
        Assert.NotEqual(installed, enabledETag);
        Assert.Equal("Enabled", (await ReadAsync(enabled)).GetProperty("state").GetString());
        Assert.True(harness.Manager.IsActivated(Id));
        Assert.Equal(1, harness.Executions);
        var status = await harness.StatusAsync();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("loaded").GetBoolean());
        Assert.Equal(enabledETag, status.GetProperty("lifecycleETag").GetString());

        // Already enabled with the current ETag: idempotent success, no reload, no revision change.
        using var again = await EnableAsync(harness.Client, Id, enabledETag, "1.0.0");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(enabledETag, again.Headers.ETag!.Tag);
        Assert.Equal(1, harness.Executions);
    }

    [Fact]
    public async Task Enable_ThatFailsActivation_Is422_ReportsTheNewState_AndLeaksNothing()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync(throwOnActivate: true);

        using var response = await EnableAsync(harness.Client, Id, installed, "1.0.0");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("activation_failed", body.RootElement.GetProperty("category").GetString());
        harness.AssertSanitized(raw);
        Assert.False(harness.Manager.IsActivated(Id));
        var status = harness.Lifecycle.GetStatus(Id)!;
        Assert.Equal(PluginLifecycleState.ActivationFailed, status.State);
        Assert.Equal(status.ETag, body.RootElement.GetProperty("eTag").GetString());
        Assert.NotEqual(installed, status.ETag);
        Assert.Equal("ActivationFailed", (await harness.StatusAsync()).GetProperty("lifecycleState").GetString());
    }

    // ---- Disable ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_UnregistersThePlugin_KeepsItInstalled_AndIsIdempotent()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();
        using (var enabled = await EnableAsync(harness.Client, Id, installed, "1.0.0"))
        {
            installed = enabled.Headers.ETag!.Tag;
        }

        using var disabled = await ActionAsync(harness.Client, Id, "disable", installed);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var etag = disabled.Headers.ETag!.Tag;
        Assert.NotEqual(installed, etag);
        Assert.Equal("InstalledDisabled", (await ReadAsync(disabled)).GetProperty("state").GetString());
        Assert.False(harness.Manager.IsActivated(Id));
        Assert.Equal(Id, Assert.Single(harness.Manager.List()).Id);

        // Already disabled with the current ETag: idempotent success and no revision change.
        using var again = await ActionAsync(harness.Client, Id, "disable", etag);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(etag, again.Headers.ETag!.Tag);
        Assert.Equal(1, harness.Executions);
    }

    // ---- ETag / stale preconditions ----------------------------------------------------------------------

    [Fact]
    public async Task StaleIfMatch_OnEveryMutation_IsA412_WithZeroMutationAndZeroExecution()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();
        string enabledETag;
        using (var enabled = await EnableAsync(harness.Client, Id, installed, "1.0.0"))
        {
            enabledETag = enabled.Headers.ETag!.Tag;
        }

        var before = harness.TakeSnapshot();

        using var staleDisable = await ActionAsync(harness.Client, Id, "disable", installed);
        using var staleEnable = await EnableAsync(harness.Client, Id, installed, "1.0.0");
        using var staleRecover = await RecoverAsync(harness.Client, Id, installed);
        using var staleReplace = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0"), installed);

        foreach (var response in new[] { staleDisable, staleEnable, staleRecover, staleReplace })
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("stale_lifecycle_version", await CategoryAsync(response));
            Assert.Equal(enabledETag, (await ReadAsync(response)).GetProperty("eTag").GetString());
        }

        harness.AssertUnchanged(before);
        Assert.True(harness.Manager.IsActivated(Id));
        Assert.Equal(PluginLifecycleState.Enabled, harness.Lifecycle.GetStatus(Id)!.State);
        Assert.Equal(enabledETag, harness.Lifecycle.GetStatus(Id)!.ETag);
        Assert.Equal(1, harness.Executions);
    }

    [Fact]
    public async Task ReadThenWrite_RaceCannotBypassTheBackend_ExactlyOneOfTwoConcurrentMutationsWins()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();
        string enabledETag;
        using (var enabled = await EnableAsync(harness.Client, Id, installed, "1.0.0"))
        {
            enabledETag = enabled.Headers.ETag!.Tag;
        }

        // Two clients read the same ETag and both act on it. The API holds no lock and does no comparison: the backend serialises them.
        var responses = await Task.WhenAll(
            ActionAsync(harness.Client, Id, "disable", enabledETag),
            ActionAsync(harness.Client, Id, "disable", enabledETag),
            ActionAsync(harness.Client, Id, "disable", enabledETag));
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.PreconditionFailed));
            Assert.Equal(PluginLifecycleState.InstalledDisabled, harness.Lifecycle.GetStatus(Id)!.State);
            Assert.False(harness.Manager.IsActivated(Id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // ---- Idempotency -----------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotencyKey_IsTransportedToTheBackend_AndAReplayReturnsTheSameLogicalResult()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();

        using var first = await EnableAsync(harness.Client, Id, installed, "1.0.0", key: "enable-key-1");
        // The same request again — now with a stale ETag on its face. Only the backend's idempotency record makes this a success.
        using var replay = await EnableAsync(harness.Client, Id, installed, "1.0.0", key: "enable-key-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(first.Headers.ETag!.Tag, replay.Headers.ETag!.Tag);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, harness.Executions);
        Assert.Equal(first.Headers.ETag.Tag, harness.Lifecycle.GetStatus(Id)!.ETag);

        // Without the key the same stale request is an ordinary stale-state failure: the key, not the API, produced the replay.
        using var noKey = await EnableAsync(harness.Client, Id, installed, "1.0.0");
        Assert.Equal(HttpStatusCode.PreconditionFailed, noKey.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_ReusedForADifferentRequest_IsAConflict_WithNoSecondMutation()
    {
        using var harness = new PluginLifecycleApiHarness();
        var installed = await harness.InstallAsync();
        string enabledETag;
        using (var first = await EnableAsync(harness.Client, Id, installed, "1.0.0", key: "shared-key"))
        {
            enabledETag = first.Headers.ETag!.Tag;
        }

        var before = harness.TakeSnapshot();

        using var otherOperation = await ActionAsync(harness.Client, Id, "disable", enabledETag, key: "shared-key");
        using var otherArchive = await UploadNewAsync(harness.Client, harness.Archive("2.0.0"), key: "shared-key");

        foreach (var response in new[] { otherOperation, otherArchive })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.Equal("idempotency_conflict", JsonDocument.Parse(raw).RootElement.GetProperty("category").GetString());
            harness.AssertSanitized(raw);
            Assert.DoesNotContain("fingerprint", raw, StringComparison.OrdinalIgnoreCase);
        }

        harness.AssertUnchanged(before);
        Assert.Equal(PluginLifecycleState.Enabled, harness.Lifecycle.GetStatus(Id)!.State);
    }

    [Fact]
    public async Task IdempotencyKey_TransportValidation_IsBoundedAndUnambiguous()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        var before = harness.TakeSnapshot();

        using var tooLong = await ActionAsync(harness.Client, Id, "disable", etag, key: new string('k', 129));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("invalid_request", await CategoryAsync(tooLong));

        using var multiple = await SendAsync(
            harness.Client, HttpMethod.Post, $"/api/plugins/{Id}/disable", headers: [("If-Match", etag), ("Idempotency-Key", "a"), ("Idempotency-Key", "b")]);
        Assert.Equal(HttpStatusCode.BadRequest, multiple.StatusCode);

        harness.AssertUnchanged(before);

        // The longest accepted key is exactly the backend bound; a blank key opts out like the existing task API.
        using var maximum = await ActionAsync(harness.Client, Id, "disable", etag, key: new string('k', 128));
        using var blank = await ActionAsync(harness.Client, Id, "disable", etag, key: "  ");
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
    }

    [Fact]
    public async Task ConcurrentUploads_WithTheSameKeyAndArchive_ProduceOneLogicalInstall()
    {
        var gate = new GateState();
        using var harness = new PluginLifecycleApiHarness(configureServices: gate.Configure);
        var archive = harness.Archive();
        gate.FirstPart = archive.Length / 2;

        // A is held mid-body at the backend's read, after it reserved the key, so B genuinely overlaps it. No sleeps: B is sent only once
        // A is provably in flight, and A is released only afterwards.
        var a = SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(archive), "application/zip",
            [("If-None-Match", "*"), ("Idempotency-Key", "same-key"), (GateState.Header, "hold")]);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var b = UploadNewAsync(harness.Client, archive, key: "same-key");
        gate.Release.SetResult();
        using var first = await a;
        using var second = await b;

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());

        // One logical backend install: one record at revision 1, one generation directory, nothing left staged.
        Assert.Equal(Id, Assert.Single(harness.Manager.List()).Id);
        Assert.Equal(1, harness.Lifecycle.GetStatus(Id)!.LifecycleVersion);
        Assert.Equal("\"plv-1\"", first.Headers.ETag!.Tag);
        Assert.Equal([Id], Directory.GetDirectories(harness.PluginsRoot).Select(path => Path.GetFileName(path)!).Where(name => name != ".lifecycle").ToArray());
        Assert.Empty(harness.WorkAreaEntries("staging"));
        Assert.Empty(harness.WorkAreaEntries("uploads"));
        Assert.Equal(0, harness.Executions);
    }

    [Fact]
    public async Task ConcurrentUploads_WithTheSameKeyAndADifferentArchive_ConflictWithoutASecondInstall()
    {
        var gate = new GateState();
        using var harness = new PluginLifecycleApiHarness(configureServices: gate.Configure);
        var first = harness.Archive("1.0.0");
        var different = harness.Archive("2.0.0");
        gate.FirstPart = first.Length / 2;

        var owner = SendAsync(
            harness.Client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(first), "application/zip",
            [("If-None-Match", "*"), ("Idempotency-Key", "same-key"), (GateState.Header, "hold")]);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var follower = UploadNewAsync(harness.Client, different, key: "same-key");
        gate.Release.SetResult();
        using var ownerResponse = await owner;
        using var followerResponse = await follower;

        Assert.Equal(HttpStatusCode.Created, ownerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, followerResponse.StatusCode);
        Assert.Equal("idempotency_conflict", await CategoryAsync(followerResponse));
        Assert.Equal("1.0.0", harness.Lifecycle.GetStatus(Id)!.PluginVersion);
        Assert.Equal(1, harness.Lifecycle.GetStatus(Id)!.LifecycleVersion);
        Assert.Equal(0, harness.Executions);
    }

    // ---- Recover ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Recover_RestoresTheActivationLkgDisabled_AndNeverActivatesOrExecutesAnything()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.InstallAsync("1.0.0");
        using (var enabled = await EnableAsync(harness.Client, Id, etag, "1.0.0"))
        {
            etag = enabled.Headers.ETag!.Tag;
        }

        using (var disabled = await ActionAsync(harness.Client, Id, "disable", etag))
        {
            etag = disabled.Headers.ETag!.Tag;
        }

        using (var replaced = await UploadForIdAsync(harness.Client, Id, harness.Archive("2.0.0", throwOnActivate: true), etag))
        {
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            etag = replaced.Headers.ETag!.Tag;
        }

        using (var failed = await EnableAsync(harness.Client, Id, etag, "2.0.0"))
        {
            Assert.Equal("activation_failed", await CategoryAsync(failed));
        }

        ReleasePluginContexts();
        var failedETag = harness.Lifecycle.GetStatus(Id)!.ETag;
        var executionsBeforeRecovery = harness.Executions;
        Assert.Equal(2, executionsBeforeRecovery); // v1 enabled, then v2's failing constructor

        // Recovery needs explicit confirmation and the current ETag.
        using var unconfirmed = await RecoverAsync(harness.Client, Id, failedETag, confirmed: false);
        using var noBody = await ActionAsync(harness.Client, Id, "recover", failedETag);
        using var stale = await RecoverAsync(harness.Client, Id, etag);
        Assert.Equal("activation_confirmation_required", await CategoryAsync(unconfirmed));
        Assert.Equal("activation_confirmation_required", await CategoryAsync(noBody));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(PluginLifecycleState.ActivationFailed, harness.Lifecycle.GetStatus(Id)!.State);

        using var recovered = await RecoverAsync(harness.Client, Id, failedETag);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var body = await ReadAsync(recovered);
        Assert.Equal("InstalledDisabled", body.GetProperty("state").GetString());
        Assert.Equal("1.0.0", body.GetProperty("version").GetString());
        Assert.NotEqual(failedETag, recovered.Headers.ETag!.Tag);
        Assert.Equal(recovered.Headers.ETag.Tag, harness.Lifecycle.GetStatus(Id)!.ETag);

        // Recovery never enables, activates, auto-confirms or runs plugin code.
        Assert.False(harness.Manager.IsActivated(Id));
        Assert.False(harness.Manager.List().Single().Enabled);
        Assert.Equal(executionsBeforeRecovery, harness.Executions);
        var status = await harness.StatusAsync();
        Assert.Equal("InstalledDisabled", status.GetProperty("lifecycleState").GetString());
        Assert.False(status.GetProperty("loaded").GetBoolean());

        // Nothing to recover from once it is InstalledDisabled again.
        using var again = await RecoverAsync(harness.Client, Id, recovered.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("state_conflict", await CategoryAsync(again));
    }

    // ---- Error sanitization ------------------------------------------------------------------------------

    [Fact]
    public async Task EveryFailureResponse_IsSanitized()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.InstallAsync();
        var responses = new List<HttpResponseMessage>
        {
            await UploadNewAsync(harness.Client, [9, 9, 9]),
            await UploadNewAsync(harness.Client, harness.Archive(sign: false)),
            await UploadNewAsync(harness.Client, harness.Archive("2.0.0")),
            await UploadForIdAsync(harness.Client, "acme.other", harness.Archive("2.0.0"), null, createOnly: true),
            await EnableAsync(harness.Client, Id, etag, "0.0.1"),
            await EnableAsync(harness.Client, "acme.unknown", etag, "1.0.0"),
            await ActionAsync(harness.Client, Id, "disable", "\"plv-77\""),
            await ActionAsync(harness.Client, Id, "disable", null),
            await RecoverAsync(harness.Client, Id, etag),
        };

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.False(response.IsSuccessStatusCode, response.StatusCode.ToString());
                var raw = await response.Content.ReadAsStringAsync();
                harness.AssertSanitized(raw);
                using var body = JsonDocument.Parse(raw);
                Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("category").GetString()));
                Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("message").GetString()));
            }
        }
    }

    [Fact]
    public async Task UnexpectedBackendFailure_IsASanitized500_WithoutAPathOrException()
    {
        using var harness = new PluginLifecycleApiHarness();
        Assert.NotNull(harness.Manager); // boot the host before tampering with the work area
        var lifecycleRoot = Path.Combine(harness.PluginsRoot, ".lifecycle");
        Directory.CreateDirectory(harness.PluginsRoot);
        if (Directory.Exists(lifecycleRoot))
        {
            Directory.Delete(lifecycleRoot, recursive: true);
        }

        await File.WriteAllTextAsync(lifecycleRoot, "not a directory"); // the work area can no longer be created

        using var response = await UploadNewAsync(harness.Client, harness.Archive());
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_failure", JsonDocument.Parse(raw).RootElement.GetProperty("category").GetString());
        harness.AssertSanitized(raw);
        Assert.DoesNotContain("not a directory", raw, StringComparison.Ordinal);
        Assert.Empty(harness.Manager.List());
    }

    // ---- Mapping ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryBackendCategory_HasExactlyOneDeterministicMapping()
    {
        var seen = new Dictionary<PluginLifecycleResultCategory, PluginLifecycleHttpMapping.Mapped>();
        foreach (var category in Enum.GetValues<PluginLifecycleResultCategory>().Where(category => category != PluginLifecycleResultCategory.Succeeded))
        {
            var first = PluginLifecycleHttpMapping.Map(category);
            Assert.Equal(first, PluginLifecycleHttpMapping.Map(category));
            Assert.False(string.IsNullOrWhiteSpace(first.Code));
            Assert.True(first.Status is >= 400 and < 600);
            seen[category] = first;
        }

        Assert.Equal(seen.Count, seen.Values.Select(mapped => mapped.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(StatusCodes.Status412PreconditionFailed, seen[PluginLifecycleResultCategory.StaleVersion].Status);
        Assert.Equal(StatusCodes.Status409Conflict, seen[PluginLifecycleResultCategory.IdempotencyConflict].Status);
        Assert.Equal(StatusCodes.Status500InternalServerError, seen[PluginLifecycleResultCategory.InternalFailure].Status);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    /// <summary>One otherwise-valid request per lifecycle mutation, so a rejection can only be about who is asking.</summary>
    private static async Task<List<HttpResponseMessage>> AllMutationsAsync(HttpClient client, PluginLifecycleApiHarness harness, string etag) =>
    [
        await UploadNewAsync(client, harness.Archive("2.0.0")),
        await UploadForIdAsync(client, Id, harness.Archive("2.0.0"), etag),
        await EnableAsync(client, Id, etag, "1.0.0"),
        await ActionAsync(client, Id, "disable", etag),
        await RecoverAsync(client, Id, etag),
    ];

    private const string MultipartContentType = "multipart/form-data; boundary=----bops-test-boundary";

    /// <summary>The body a browser form upload would send, built by hand so no content object outlives the request.</summary>
    private static byte[] MultipartBody(byte[] archive)
    {
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("------bops-test-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"plugin.zip\"\r\nContent-Type: application/zip\r\n\r\n"));
        body.Write(archive);
        body.Write(Encoding.ASCII.GetBytes("\r\n------bops-test-boundary--\r\n"));
        return body.ToArray();
    }

    /// <summary>A body of <c>length</c> zero bytes streamed with no declared Content-Length, never materialised in memory.</summary>
    private sealed class GeneratedStreamContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[16 * 1024];
            for (long written = 0; written < length; written += chunk.Length)
            {
                await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, length - written)));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A deterministic mid-upload stall at the point the backend really reads: for a request carrying <see cref="Header"/>, the request
    /// body serves the first <see cref="FirstPart"/> bytes, signals <see cref="Reached"/> when the backend asks for more, then waits for
    /// <see cref="Release"/> (or, in <see cref="AbortAtGate"/> mode, for the caller to abort the request).
    /// </summary>
    private sealed class GateState
    {
        internal const string Header = "X-Test-Gate";
        internal const string HideLengthHeader = "X-Test-Hide-Length";

        internal int FirstPart { get; set; }

        internal bool AbortAtGate { get; init; }

        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Configure(IServiceCollection services) => services.AddTransient<IStartupFilter>(_ => new Filter(this));

        private sealed class Filter(GateState state) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, pipeline) =>
                {
                    if (context.Request.Headers.ContainsKey(HideLengthHeader))
                    {
                        // What an untruthful or absent Content-Length looks like to the app, on a server that enforces no limit of its own.
                        context.Request.ContentLength = null;
                        context.Features.Set<IHttpMaxRequestBodySizeFeature>(null);
                    }

                    var gated = context.Request.Headers.ContainsKey(Header);
                    if (gated)
                    {
                        context.Request.Body = new GatedBody(context.Request.Body, state);
                    }

                    try
                    {
                        await pipeline(context);
                    }
                    finally
                    {
                        if (gated)
                        {
                            state.Finished.TrySetResult();
                        }
                    }
                });
                next(app);
            };
        }

        private sealed class GatedBody(Stream inner, GateState state) : Stream
        {
            private int _delivered;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_delivered >= state.FirstPart)
                {
                    state.Reached.TrySetResult();
                    if (state.AbortAtGate)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    else
                    {
                        await state.Release.Task.WaitAsync(cancellationToken);
                    }

                    return await inner.ReadAsync(buffer, cancellationToken);
                }

                var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, state.FirstPart - _delivered)], cancellationToken);
                _delivered += read;
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
