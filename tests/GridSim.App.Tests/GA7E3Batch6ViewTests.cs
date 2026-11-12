using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using GridSim.App.Tabs;
using GridSim.Core.IO;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Market;
using GridSim.Ui.Tabs.Operator;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// GR-765: the OPERATOR "System" strip and the MARKET "Prices" row of the Avalonia app. Each value the model
/// computes but no tile showed (response, net interconnector, voltage band; system sell price, day-ahead, NIV) is
/// now shown, and each text box is bound to the model property (changing the property changes the box).
/// </summary>
public sealed class GA7E3Batch6ViewTests
{
    private static T Host<T>(T view) where T : Control
    {
        var window = new Window { Width = 1700, Height = 1000, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static string Text(Control view, string name)
    {
        TextBlock t = view.FindControl<TextBlock>(name) ?? throw new InvalidOperationException($"no TextBlock {name}");
        return t.Text ?? "";
    }

    [AvaloniaFact]
    public void Operator_system_strip_shows_response_net_interconnector_and_voltage_band_bound_to_the_model()
    {
        var model = new OperatorModel();
        OperatorView view = Host(new OperatorView { DataContext = model });

        // Before any data: the model's placeholders.
        Assert.Equal(" - ", Text(view, "ResponseText"));
        Assert.Equal(" - ", Text(view, "NetInterconnectorText"));
        Assert.Equal(" - ", Text(view, "VoltageBandText"));

        model.SetOperatorData(new BridgeProtocol.OperatorBlock { ResponseMw = 1234.4, NetInterconnectorMw = -2500.0 }, "12:00");
        model.VoltageBand = "0.950-1.050 pu";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1234 MW", model.ResponseMw);
        Assert.Equal(model.ResponseMw, Text(view, "ResponseText"));
        Assert.Equal("-2.50 GW", model.NetInterconnector);
        Assert.Equal(model.NetInterconnector, Text(view, "NetInterconnectorText"));
        Assert.Equal("0.950-1.050 pu", Text(view, "VoltageBandText"));
    }

    [AvaloniaFact]
    public void Market_prices_row_shows_system_sell_price_day_ahead_and_niv_bound_to_the_model()
    {
        var model = new MarketModel();
        MarketView view = Host(new MarketView { DataContext = model });

        Assert.Equal(" - ", Text(view, "SystemSellPriceText"));
        Assert.Equal(" - ", Text(view, "DayAheadText"));
        Assert.Equal(" - ", Text(view, "NivText"));

        model.SetMarketData(new BridgeProtocol.MarketBlock { SystemSellPriceGbpMwh = 85.456, DayAheadGbpMwh = 70.0, NetImbalanceVolumeMwh = -123.0 });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("GBP 85.46", model.SystemSellPrice);
        Assert.Equal(model.SystemSellPrice, Text(view, "SystemSellPriceText"));
        Assert.Equal("GBP 70.00", model.DayAhead);
        Assert.Equal(model.DayAhead, Text(view, "DayAheadText"));
        Assert.Equal("-123 MWh", model.Niv);
        Assert.Equal(model.Niv, Text(view, "NivText"));

        // The NIV colour is the model's token, exactly (short system = red).
        UiColour c = model.NivColour;
        Color shown = Assert.IsAssignableFrom<ISolidColorBrush>(view.FindControl<TextBlock>("NivText")!.Foreground).Color;
        Assert.Equal(Color.FromArgb(c.A, c.R, c.G, c.B), shown);
    }
}
