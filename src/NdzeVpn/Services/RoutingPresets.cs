namespace NdzeVpn.Services;

/// <summary>
/// Curated domain lists used by Smart mode.
///
/// These exist so routing keeps working even when the downloaded geosite.dat is stale, missing,
/// or built by a source that does not ship the tag we asked for. Xray refuses to start if a
/// <c>geosite:</c> tag is unknown, so every geosite reference is treated as optional and
/// validated (see <see cref="XrayProcess.TestConfigAsync"/>), while these literal lists always apply.
/// </summary>
public static class RoutingPresets
{
    /// <summary>TLD suffixes that are Russian by definition. Cheap, exact, no data file needed.</summary>
    public static readonly string[] RussianTlds =
    [
        "domain:ru", "domain:su", "domain:рф", "domain:xn--p1ai",
        "domain:moscow", "domain:xn--80adxhks", "domain:tatar", "domain:by", "domain:kz"
    ];

    /// <summary>
    /// Services that refuse or misbehave on foreign IPs: banking, government, marketplaces,
    /// delivery, big RU media. These stay direct even if you set the default action to Proxy.
    /// </summary>
    public static readonly string[] RussianEssential =
    [
        // Banks / payments
        "domain:sberbank.ru", "domain:sber.ru", "domain:sberbank.com", "domain:sbrf.ru",
        "domain:tinkoff.ru", "domain:tbank.ru", "domain:vtb.ru", "domain:alfabank.ru",
        "domain:gazprombank.ru", "domain:raiffeisen.ru", "domain:psbank.ru", "domain:open.ru",
        "domain:rshb.ru", "domain:mkb.ru", "domain:sovcombank.ru", "domain:pochtabank.ru",
        "domain:mironline.ru", "domain:nspk.ru", "domain:sbp.nspk.ru", "domain:qiwi.com",
        "domain:yoomoney.ru", "domain:cbr.ru",

        // Government
        "domain:gosuslugi.ru", "domain:nalog.ru", "domain:nalog.gov.ru", "domain:pfr.gov.ru",
        "domain:mos.ru", "domain:gov.ru", "domain:mvd.ru", "domain:fssp.gov.ru",
        "domain:rosreestr.ru", "domain:mchs.gov.ru", "domain:edu.ru",

        // Marketplaces / services
        "domain:ozon.ru", "domain:wildberries.ru", "domain:wb.ru", "domain:avito.ru",
        "domain:dns-shop.ru", "domain:mvideo.ru", "domain:eldorado.ru", "domain:citilink.ru",
        "domain:lamoda.ru", "domain:sbermarket.ru", "domain:samokat.ru", "domain:delivery-club.ru",
        "domain:cdek.ru", "domain:pochta.ru", "domain:boxberry.ru",

        // Yandex / VK / Mail.ru ecosystems
        "domain:yandex.ru", "domain:yandex.net", "domain:yandex.com", "domain:ya.ru",
        "domain:yastatic.net", "domain:yandex.st", "domain:kinopoisk.ru",
        "domain:vk.com", "domain:vk.ru", "domain:vk-cdn.net", "domain:vkuser.net",
        "domain:userapi.com", "domain:vkuservideo.net", "domain:vkontakte.ru",
        "domain:mail.ru", "domain:imgsmail.ru", "domain:ok.ru", "domain:odnoklassniki.ru",
        "domain:rutube.ru", "domain:smotrim.ru", "domain:ivi.ru", "domain:okko.tv",
        "domain:kion.ru", "domain:premier.one", "domain:more.tv", "domain:wink.ru",

        // Transport / telecom / misc
        "domain:rzd.ru", "domain:aeroflot.ru", "domain:s7.ru", "domain:pobeda.aero",
        "domain:2gis.ru", "domain:2gis.com", "domain:hh.ru", "domain:drom.ru", "domain:auto.ru",
        "domain:mts.ru", "domain:beeline.ru", "domain:megafon.ru", "domain:tele2.ru",
        "domain:rt.ru", "domain:dom.ru", "domain:gismeteo.ru", "domain:rambler.ru",
        "domain:habr.com", "domain:sbis.ru", "domain:kontur.ru", "domain:1c.ru",
        "domain:litres.ru", "domain:labirint.ru", "domain:chitai-gorod.ru"
    ];

    /// <summary>
    /// Everything Russia blocks or throttles that people actually use daily. Listed by name so it
    /// works the moment you connect, without waiting on a geosite update.
    /// </summary>
    public static readonly string[] BlockedPopular =
    [
        // Google video stack (throttled, not formally blocked — same practical result)
        "domain:youtube.com", "domain:youtu.be", "domain:ytimg.com", "domain:googlevideo.com",
        "domain:ggpht.com", "domain:youtube-nocookie.com", "domain:yt3.ggpht.com",
        "domain:withyoutube.com", "domain:youtubekids.com", "domain:youtubei.googleapis.com",

        // Discord
        "domain:discord.com", "domain:discordapp.com", "domain:discord.gg", "domain:discordapp.net",
        "domain:discord.media", "domain:discordcdn.com", "domain:discordstatus.com",

        // Meta
        "domain:instagram.com", "domain:cdninstagram.com", "domain:instagr.am",
        "domain:facebook.com", "domain:fbcdn.net", "domain:fb.com", "domain:fbsbx.com",
        "domain:meta.com", "domain:threads.net", "domain:whatsapp.com", "domain:whatsapp.net",

        // X / Twitter
        "domain:twitter.com", "domain:x.com", "domain:twimg.com", "domain:t.co", "domain:twitpic.com",

        // Telegram
        "domain:telegram.org", "domain:telegram.me", "domain:t.me", "domain:telesco.pe",
        "domain:tdesktop.com", "domain:telegra.ph", "domain:comments.app",

        // Streaming / media
        "domain:spotify.com", "domain:scdn.co", "domain:spotifycdn.com", "domain:soundcloud.com",
        "domain:sndcdn.com", "domain:twitch.tv", "domain:ttvnw.net", "domain:jtvnw.net",
        "domain:netflix.com", "domain:nflxvideo.net", "domain:nflximg.net",
        "domain:vimeo.com", "domain:vimeocdn.com", "domain:deezer.com", "domain:tidal.com",

        // AI / dev / work
        "domain:openai.com", "domain:chatgpt.com", "domain:oaistatic.com", "domain:oaiusercontent.com",
        "domain:anthropic.com", "domain:claude.ai", "domain:perplexity.ai", "domain:midjourney.com",
        "domain:huggingface.co", "domain:gemini.google.com", "domain:bard.google.com",
        "domain:notion.so", "domain:notion.site", "domain:figma.com", "domain:medium.com",
        "domain:slack.com", "domain:atlassian.net", "domain:linkedin.com", "domain:licdn.com",
        "domain:patreon.com", "domain:coinbase.com", "domain:binance.com",

        // Social / forums
        "domain:reddit.com", "domain:redd.it", "domain:redditstatic.com", "domain:redditmedia.com",
        "domain:pinterest.com", "domain:pinimg.com", "domain:tumblr.com", "domain:deviantart.com",
        "domain:quora.com", "domain:4chan.org", "domain:9gag.com", "domain:imgur.com",

        // Trackers / archives
        "domain:rutracker.org", "domain:rutracker.net", "domain:nnmclub.to", "domain:flibusta.is",
        "domain:archive.org", "domain:libgen.is", "domain:sci-hub.se", "domain:thepiratebay.org",

        // News blocked in RU
        "domain:bbc.com", "domain:bbc.co.uk", "domain:dw.com", "domain:meduza.io",
        "domain:svoboda.org", "domain:currenttime.tv", "domain:theins.ru", "domain:novayagazeta.eu",
        "domain:mediazona.care", "domain:proekt.media", "domain:the-village.ru", "domain:zona.media",
        "domain:rfi.fr", "domain:euronews.com", "domain:nytimes.com", "domain:washingtonpost.com",

        // VPN-adjacent / infra people need working
        "domain:speedtest.net", "domain:cloudflare.com", "domain:cloudflare-dns.com",
        "domain:steamcommunity.com", "domain:epicgames.com", "domain:ea.com", "domain:origin.com",
        "domain:ubisoft.com", "domain:xbox.com", "domain:playstation.com", "domain:nintendo.com"
    ];

    /// <summary>Discord voice/RTC endpoints. Voice is UDP, so this only bites in TUN mode.</summary>
    public static readonly string[] DiscordVoice =
    [
        "domain:discord.media", "domain:discord.gg", "regexp:.*\\.discord\\.media$",
        "regexp:^[a-z]+[0-9]*\\.discord\\.gg$", "regexp:.*\\.discordapp\\.net$"
    ];

    /// <summary>
    /// Optional geosite tags, tried in order and silently dropped if the installed geosite.dat
    /// does not define them. Different rule sources name these differently, hence the list.
    /// </summary>
    public static readonly string[] OptionalBlockedGeoSites =
    [
        "geosite:refilter", "geosite:ru-blocked", "geosite:russia-blocked",
        "geosite:geolocation-!ru", "geosite:category-gfw"
    ];

    public static readonly string[] OptionalRussianGeoSites =
    [
        "geosite:category-ru", "geosite:ru", "geosite:russia-inside", "geosite:category-ru-gov"
    ];

    public static readonly string[] OptionalAdGeoSites =
    [
        "geosite:category-ads-all", "geosite:category-ads"
    ];
}
