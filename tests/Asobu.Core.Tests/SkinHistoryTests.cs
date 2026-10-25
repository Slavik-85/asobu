using System.Net;
using Asobu.Core.Skins;

namespace Asobu.Core.Tests;

/// <summary>A player's past skins, read from crafty.gg's player endpoint in the shape it really returns.</summary>
public class SkinHistoryTests
{
    private const string Player = """
        {"success":true,"data":{"uuid":"069a79f4-44e9-4726-a5be-fca90e38aaf5","username":"Notch",
          "skins":[
            {"hash":"ca93f6fc40488f1877cda94a830b54e9f6f54ab58a5453bad5c947726dd1f473","texture":"iVBOR","slim":true,"changed_at":"2025-01-13T08:20:32.200+01:00"},
            {"hash":"5468f1e6af9a040d8b4a2a0503491b1dffeec6203ff8df29bd927c27158f3a5d","slim":false,"changed_at":null},
            {"hash":"5468f1e6af9a040d8b4a2a0503491b1dffeec6203ff8df29bd927c27158f3a5d","slim":false},
            {"hash":null,"slim":false}
          ]}}
        """;

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Past_skins_come_back_newest_first_with_their_arms_and_no_repeats()
    {
        var handler = new Canned(HttpStatusCode.OK, Player);

        var history = await new SkinService(new HttpClient(handler)).HistoryAsync("069a79f4-44e9-4726-a5be-fca90e38aaf5");

        Assert.EndsWith("/069a79f444e94726a5befca90e38aaf5", handler.Asked!.AbsoluteUri);
        Assert.Equal(2, history.Count);
        Assert.Equal(("ca93f6fc40488f1877cda94a830b54e9f6f54ab58a5453bad5c947726dd1f473", "Jan 2025", SkinModel.Slim),
            (history[0].Texture, history[0].Name, history[0].Model));
        Assert.Equal(SkinModel.Classic, history[1].Model);
        Assert.Null(history[1].Name);
    }

    [Fact]
    public async Task A_player_never_seen_has_no_history_rather_than_an_error()
    {
        var service = new SkinService(new HttpClient(new Canned(HttpStatusCode.NotFound, """{"success":false}""")));

        Assert.Empty(await service.HistoryAsync("0123456789abcdef0123456789abcdef"));
    }
}
