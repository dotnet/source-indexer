namespace Microsoft.SourceBrowser.HtmlGenerator
{
    // Local copy of the members of Visual Studio's StandardGlyphGroup/StandardGlyphItem
    // enums that SymbolIdService uses to compute icon numbers. The integer values match
    // the VS SDK exactly (they index the checked-in icon set), so vendoring them lets us
    // drop the Microsoft.VisualStudio.Language.Intellisense package -- which only shipped
    // as .NETFramework (NU1701) and dragged in StreamJsonRpc, MessagePack and Newtonsoft.Json.
    internal enum StandardGlyphGroup
    {
        GlyphGroupClass = 0,
        GlyphGroupConstant = 6,
        GlyphGroupDelegate = 12,
        GlyphGroupEnum = 18,
        GlyphGroupEnumMember = 24,
        GlyphGroupEvent = 30,
        GlyphGroupField = 42,
        GlyphGroupInterface = 48,
        GlyphGroupMethod = 72,
        GlyphGroupModule = 84,
        GlyphGroupNamespace = 90,
        GlyphGroupOperator = 96,
        GlyphGroupProperty = 102,
        GlyphGroupStruct = 108,
        GlyphGroupType = 126,
        GlyphGroupVariable = 138,
        GlyphGroupIntrinsic = 150,
        GlyphGroupError = 186,
        GlyphAssembly = 192,
        GlyphVBProject = 194,
        GlyphCoolProject = 196,
        GlyphOpenFolder = 201,
        GlyphCSharpFile = 204,
        GlyphCSharpExpansion = 205,
        GlyphKeyword = 206,
        GlyphReference = 208,
        GlyphExtensionMethod = 220,
        GlyphExtensionMethodInternal = 221,
        GlyphExtensionMethodProtected = 223,
        GlyphExtensionMethodPrivate = 224,
        GlyphCompletionWarning = 236,
    }

    internal enum StandardGlyphItem
    {
        GlyphItemPublic = 0,
        GlyphItemFriend = 2,
        GlyphItemProtected = 3,
        GlyphItemPrivate = 4,
    }
}
