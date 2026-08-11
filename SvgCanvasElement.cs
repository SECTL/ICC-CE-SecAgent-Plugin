using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ink_Canvas.SecAgent.Plugin;

internal sealed class SvgCanvasElement : Border
{
    private readonly WebBrowser _browser = new();
    private readonly string _document;

    public SvgCanvasElement(string svg)
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Focusable = false;
        _document = WrapSvgDocument(svg);
        _browser.IsHitTestVisible = false;
        _browser.Focusable = false;
        Child = _browser;
        Loaded += (_, _) => _browser.NavigateToString(_document);
    }

    private static string WrapSvgDocument(string svg)
        => "<!doctype html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"></head>"
         + "<body style=\"margin:0;padding:0;overflow:hidden;background:transparent;\">"
         + svg
         + "</body></html>";
}
