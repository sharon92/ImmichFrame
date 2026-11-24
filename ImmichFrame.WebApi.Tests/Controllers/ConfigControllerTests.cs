using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers
{
    [TestFixture]
    public class ConfigControllerTests
    {
        private WebApplicationFactory<Program> _factory;
        private GeneralSettings _generalSettings;

        [SetUp]
        public void Setup()
        {
            var generalSettings = new GeneralSettings
            {
                Interval = 33,
                TransitionDuration = 2.5,
                DownloadImages = true,
                RenewImagesDuration = 77,
                ShowClock = false,
                ClockFormat = "HH:mm:ss",
                ClockDateFormat = "yyyy-MM-dd",
                ShowPhotoDate = false,
                ShowProgressBar = false,
                PhotoDateFormat = "dd.MM.yyyy",
                ShowImageDesc = false,
                ShowPeopleDesc = false,
                ShowTagsDesc = false,
                ShowAlbumName = false,
                ShowImageLocation = false,
                ImageLocationFormat = "City",
                PrimaryColor = "#112233",
                SecondaryColor = "#445566",
                Style = "blur",
                BaseFontSize = "18px",
                ShowWeatherDescription = false,
                WeatherIconUrl = "https://example.com/{IconId}.png",
                ImageZoom = false,
                ImagePan = true,
                ImageFill = true,
                PlayAudio = true,
                Layout = "single",
                Language = "de",
                // Server-only values that must never surface in the client config:
                WeatherApiKey = "secret-weather-key",
                WeatherLatLong = "1.23,4.56",
                UnitSystem = "metric",
                Webhook = "https://webhook.example.com/secret-hook",
                AuthenticationSecret = "secret-auth-token",
                AdminPassword = "secret-admin-password",
                Webcalendars = new List<string> { "https://calendar.example.com/secret.ics" },
                RefreshAlbumPeopleInterval = 8,
            };
            _generalSettings = generalSettings;

            var serverSettings = new ServerSettings
            {
                GeneralSettingsImpl = generalSettings,
                AccountsImpl = new List<ServerAccountSettings>
                {
                    new()
                    {
                        ImmichServerUrl = "http://mock-immich-server.com",
                        ApiKey = "secret-api-key",
                    }
                }
            };

            var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.UseMockHandler(versionHandler);

                        services.AddSingleton<ISettingsProvider>(new StaticSettingsProvider(serverSettings));
                        services.AddSingleton<IServerSettings>(serverSettings);
                        services.AddSingleton<IGeneralSettings>(generalSettings);
                    });
                });
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
        }

        [Test]
        public async Task CloudflareFrameUsesItsOwnCredentialAndReceivesCommandsInOrder()
        {
            const string deviceKey = "test-frame-key-with-at-least-32-characters";
            const string path = "/api/cloudflare/frame";
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
                (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CLOUDFLARE_FRAME_KEY"] = deviceKey
                })));
            using var browser = factory.CreateClient();
            browser.DefaultRequestHeaders.Add("Authorization", "Bearer secret-auth-token");
            using var frame = factory.CreateClient();
            frame.DefaultRequestHeaders.Add("Authorization", "Bearer secret-auth-token");
            frame.DefaultRequestHeaders.Add("X-ImmichFrame-Device-Key", deviceKey);
            var id = Guid.NewGuid();
            var status = new { frame = id, current = (string?)null };
            const string commands = "/api/controls/command";

            Assert.That((await browser.PostAsJsonAsync(path + "/check-in", status)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await browser.PostAsJsonAsync(commands, new { device = "cloudflare", command = "next" })).StatusCode,
                Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That((await frame.GetAsync(path + "/config")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await frame.GetAsync(path + "/gallery?limit=1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await frame.PostAsJsonAsync(path + "/check-in", status)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            foreach (var command in new[] { "next", "previous" })
                Assert.That((await browser.PostAsJsonAsync(commands, new { device = "cloudflare", command })).StatusCode,
                    Is.EqualTo(HttpStatusCode.Accepted));

            foreach (var command in new[] { "next", "previous" })
            {
                var response = await frame.PostAsJsonAsync(path + "/check-in", status);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.That(body.GetProperty("command").GetProperty("command").GetString(), Is.EqualTo(command));
            }

            var empty = await frame.PostAsJsonAsync(path + "/check-in", status);
            var emptyBody = await empty.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(emptyBody.GetProperty("command").ValueKind, Is.EqualTo(JsonValueKind.Null));

            var playback = await browser.PostAsJsonAsync(commands, new { device = "cloudflare", command = "status" });
            Assert.That((await playback.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("frame").GetGuid(), Is.EqualTo(id));
        }

        [Test]
        public async Task GetConfig_ReturnsSerializedClientSettings()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/api/Config");

            response.EnsureSuccessStatusCode();
            var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            var expected = JsonSerializer.SerializeToNode(
                new ClientSettingsDto(_generalSettings),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.That(JsonNode.DeepEquals(actual, expected), Is.True,
                () => $"Expected: {expected?.ToJsonString()}\nActual: {actual?.ToJsonString()}");
        }

        [Test]
        public async Task GetConfig_ContainsNoSecrets()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/api/Config");

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Not.Contain("secret-auth-token"));
                Assert.That(json, Does.Not.Contain("secret-weather-key"));
                Assert.That(json, Does.Not.Contain("secret-api-key"));
                Assert.That(json, Does.Not.Contain("secret-hook"));
                Assert.That(json, Does.Not.Contain("secret.ics"));
                Assert.That(json, Does.Not.Contain("secret-admin-password"));
                Assert.That(json, Does.Not.Contain("authenticationSecret"));
                Assert.That(json, Does.Not.Contain("adminPassword"));
                Assert.That(json, Does.Not.Contain("weatherApiKey"));
                Assert.That(json, Does.Not.Contain("apiKey"));
                Assert.That(json, Does.Not.Contain("webhook"));
                Assert.That(json, Does.Not.Contain("webcalendars"));
            });
        }
    }
}
