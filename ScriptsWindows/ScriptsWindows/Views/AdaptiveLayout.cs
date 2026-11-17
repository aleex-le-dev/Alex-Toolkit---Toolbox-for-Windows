using System.Windows;
using System.Windows.Controls;

namespace ScriptsWindows.Views;

/// <summary>
/// Dimensions communes de la grille de cartes d'outils.
/// </summary>
public static class CardMetrics
{
    /// <summary>Largeur minimale d'une carte : en dessous, une colonne de moins.</summary>
    public const double MinCardWidth = 300;
    /// <summary>Espace entre deux cartes (horizontal et vertical).</summary>
    public const double Gap = 12;

    /// <summary>Nombre de colonnes et largeur de carte qui remplissent exactement <paramref name="width"/>.</summary>
    public static (int Columns, double CardWidth) Fit(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) return (1, MinCardWidth);
        int columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinCardWidth + Gap)));
        double cardWidth = Math.Max(MinCardWidth * 0.8, (width - (columns - 1) * Gap) / columns);
        return (columns, Math.Floor(cardWidth));
    }
}

/// <summary>
/// Grille de cartes à colonnes égales qui remplit toute la largeur.
/// Seule, elle calcule ses colonnes d'après la largeur disponible ; dans un <see cref="SectionFlowPanel"/>,
/// colonnes et largeur lui sont imposées pour rester alignée sur la grille de la page.
/// </summary>
public class CardGridPanel : Panel
{
    internal int? FixedColumns;
    internal double? FixedCardWidth;

    internal void SetGeometry(int columns, double cardWidth)
    {
        if (FixedColumns == columns && FixedCardWidth == cardWidth) return;
        FixedColumns = columns;
        FixedCardWidth = cardWidth;
        InvalidateMeasure();
    }

    private (int Columns, double CardWidth) Geometry(double availableWidth)
        => FixedColumns is int c && FixedCardWidth is double w ? (c, w) : CardMetrics.Fit(availableWidth);

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var (columns, cardWidth) = Geometry(availableSize.Width);
        double rowHeight = 0, total = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == visible.Count - 1)
            {
                total += rowHeight + (i == visible.Count - 1 ? 0 : CardMetrics.Gap);
                rowHeight = 0;
            }
        }
        int used = Math.Min(columns, Math.Max(1, visible.Count));
        double width = FixedColumns != null ? used * cardWidth + (used - 1) * CardMetrics.Gap
                                            : double.IsInfinity(availableSize.Width) ? used * (cardWidth + CardMetrics.Gap) : availableSize.Width;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var (columns, cardWidth) = Geometry(finalSize.Width);
        double y = 0;
        for (int start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            double h = row.Max(c => c.DesiredSize.Height);
            for (int j = 0; j < row.Count; j++)
                row[j].Arrange(new Rect(j * (cardWidth + CardMetrics.Gap), y, cardWidth, h));
            y += h + CardMetrics.Gap;
        }
        return finalSize;
    }
}

/// <summary>
/// Place les sous-catégories côte à côte sur une grille de colonnes commune : chaque section occupe autant
/// de colonnes que de cartes (au plus la largeur de la page) et la suivante se range à côté si elle tient.
/// Plus de vide à droite d'une sous-catégorie de 1 ou 2 outils.
/// </summary>
public class SectionFlowPanel : Panel
{
    /// <summary>Nombre de cartes de la section (= colonnes souhaitées).</summary>
    public static readonly DependencyProperty CardCountProperty = DependencyProperty.RegisterAttached(
        "CardCount", typeof(int), typeof(SectionFlowPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetCardCount(UIElement e, int value) => e.SetValue(CardCountProperty, value);
    public static int GetCardCount(UIElement e) => (int)e.GetValue(CardCountProperty);

    /// <summary>Grille de cartes de la section, alignée sur la grille de la page.</summary>
    public static readonly DependencyProperty CardGridProperty = DependencyProperty.RegisterAttached(
        "CardGrid", typeof(CardGridPanel), typeof(SectionFlowPanel), new PropertyMetadata(null));

    public static void SetCardGrid(UIElement e, CardGridPanel value) => e.SetValue(CardGridProperty, value);
    public static CardGridPanel? GetCardGrid(UIElement e) => (CardGridPanel?)e.GetValue(CardGridProperty);

    /// <summary>Espace vertical entre deux lignes de sections.</summary>
    public double RowSpacing { get; set; } = 18;

    private readonly List<(UIElement Child, int Column, int Span, int Row)> _layout = new();
    private readonly List<double> _rowHeights = new();
    private double _cardWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, cardWidth) = CardMetrics.Fit(availableSize.Width);
        _cardWidth = cardWidth;
        _layout.Clear();
        _rowHeights.Clear();

        // Remplissage « première place libre » : chaque ligne prend les sections dans l'ordre, et si la suivante
        // ne tient pas, une section plus loin qui tient dans l'espace restant vient combler le trou.
        var pending = InternalChildren.Cast<UIElement>()
            .Where(c => c.Visibility != Visibility.Collapsed)
            .Select(c => (Child: c, Span: Math.Clamp(GetCardCount(c), 1, columns)))
            .ToList();

        int row = 0;
        while (pending.Count > 0)
        {
            _rowHeights.Add(0);
            int col = 0;
            for (int i = 0; i < pending.Count && col < columns;)
            {
                var (child, span) = pending[i];
                if (col + span > columns) { i++; continue; }

                GetCardGrid(child)?.SetGeometry(span, cardWidth);
                double width = span * cardWidth + (span - 1) * CardMetrics.Gap;
                child.Measure(new Size(width, double.PositiveInfinity));
                _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
                _layout.Add((child, col, span, row));
                col += span;
                pending.RemoveAt(i);
            }
            row++;
        }

        double height = _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowSpacing;
        double fullWidth = columns * cardWidth + (columns - 1) * CardMetrics.Gap;
        return new Size(double.IsInfinity(availableSize.Width) ? fullWidth : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rowTops = new List<double>();
        double y = 0;
        foreach (var h in _rowHeights) { rowTops.Add(y); y += h + RowSpacing; }

        foreach (var (child, column, span, row) in _layout)
        {
            double x = column * (_cardWidth + CardMetrics.Gap);
            double w = span * _cardWidth + (span - 1) * CardMetrics.Gap;
            child.Arrange(new Rect(x, rowTops[row], w, child.DesiredSize.Height));
        }
        return finalSize;
    }
}
