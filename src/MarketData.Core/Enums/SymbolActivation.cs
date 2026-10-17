namespace MarketData.Core.Enums;

public enum SymbolActivation
{
    /// <summary>The symbol did not exist and was inserted.</summary>
    Created,

    /// <summary>The symbol existed as inactive and was reactivated; its history is kept.</summary>
    Reactivated,

    /// <summary>The symbol was already active; nothing changed.</summary>
    AlreadyActive,
}
