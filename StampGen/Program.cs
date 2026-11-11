using System.Diagnostics;
using System.Globalization;
using StampGen.Components;
using StampGen.Services;

// StampGen — точка входа: локальный сервер Blazor (http://127.0.0.1:5317…5322).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.

// Числа всегда с точкой (независимо от региональных настроек Windows).
var invariant = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = invariant;
CultureInfo.DefaultThreadCurrentUICulture = invariant;

var builder = WebApplication.CreateBuilder(args);

// v2.2: знак охраны прав владельца — статус подписи в консоль при каждом запуске.
OwnerGuard.Announce();

// Сервисы контейнера
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// v1.7: библиотеки штампов (папка stamp/ рядом с программой) — вместо вшитого набора.
// Программа стартует БЕЗ оттисков: пользователь импортирует zip-библиотеки сам.
builder.Services.AddSingleton<StampLibrary>();
builder.Services.AddScoped<StampState>();

// Шрифты для текстовых оттисков
TextGlyphs.SetFontsDir(Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "fonts"));

// Порт: --port=N, STAMPGEN_PORT или первый свободный из диапазона
int port = 0;
foreach (var a in args)
    if (a.StartsWith("--port=", StringComparison.Ordinal) && int.TryParse(a[7..], out var p)) port = p;
if (port == 0 && int.TryParse(Environment.GetEnvironmentVariable("STAMPGEN_PORT"), out var ep)) port = ep;
if (port == 0)
{
    foreach (var candidate in new[] { 5317, 5318, 5319, 5320, 5321, 5322 })
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
            l.Start(); l.Stop();
            port = candidate;
            break;
        }
        catch { /* занят — пробуем следующий */ }
    }
}
if (port == 0) port = 5317;

var app = builder.Build();

// v1.7: НАБОРОВ ОТТИСКОВ ВШИТО БОЛЬШЕ НЕТ — программа стартует пустой.
// Пользователь сам импортирует библиотеки (кнопка «Добавить штампы» / окно
// «Библиотеки») — всё хранится в папке stamp/ рядом с программой.

// Конвейер обработки запросов
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

// v1.4: файлы, созданные в wwwroot ВО ВРЕМЯ работы (распакованные наборы оттисков),
// не входят в манифест MapStaticAssets — отдаём их классическим файловым middleware.
app.UseStaticFiles();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// v1.7: раздача svg-штампов из папки stamp/ (она вне wwwroot — руками не достать,
// а программе нужно показывать миниатюры в браузере).
// v2.2: параметры маршрута читаются из HttpContext.GetRouteValue, а не через
// привязку по имени — обфускация приватных лямбд (build-exe.ps1 -Obfuscate)
// не ломает этот эндпоинт.
var stampLib = app.Services.GetRequiredService<StampLibrary>();
var stampRoot = Path.GetFullPath(stampLib.RootDir);
app.MapGet("/stamp-file/{libId}/{*path}", (HttpContext http) =>
{
    try
    {
        var libId = http.GetRouteValue("libId") as string ?? "";
        var path = (http.GetRouteValue("path") as string ?? "").Replace('/', Path.DirectorySeparatorChar);
        var libDir = Path.GetFullPath(Path.Combine(stampRoot, libId));
        if (!libDir.StartsWith(stampRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !Directory.Exists(libDir))
            return Results.NotFound();
        var full = Path.GetFullPath(Path.Combine(libDir, path));
        if (!full.StartsWith(libDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(full))
            return Results.NotFound();
        return Results.File(full, "image/svg+xml");
    }
    catch { return Results.NotFound(); }
});

// Открытие браузера при запуске готового exe (отключается: --no-browser или STAMPGEN_NO_BROWSER=1)
bool openBrowser = !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase)
                   && Environment.GetEnvironmentVariable("STAMPGEN_NO_BROWSER") != "1"
                   && !Debugger.IsAttached;
string url = $"http://127.0.0.1:{port}";
if (openBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start {url}") { CreateNoWindow = true });
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
        }
        catch { /* нет браузера — адрес покажем в консоли */ }
    });
}

// © Guoud (iljamakeev@yandex.ru). Все права защищены. Коммерческое
// распространение и использование без разрешения правообладателя запрещены
// (LICENSE.md).
Console.WriteLine("StampGen запущен: " + url + (openBrowser ? " (браузер откроется сам)" : ""));
app.Run(url);
