namespace SettlementRecon.Monetary;

/// <summary>How a provider writes decimal amounts.</summary>
public enum DecimalStyle
{
    /// <summary><c>1234.56</c> or <c>1,234.56</c>.</summary>
    Dot,

    /// <summary><c>1234,56</c>, <c>1.234,56</c> or <c>1 234,56</c>, common in continental European exports.</summary>
    Comma,
}
