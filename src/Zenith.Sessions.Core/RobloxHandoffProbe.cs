using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>One pinned game-page diagnostic, never native launch or account/arrival evidence.</summary>
public sealed record RobloxHandoffProbeContext(Guid ProfileId,long ExpectedUserId,long AuthorizationGeneration,long PlaceId)
{
    [JsonIgnore] public bool IsValid=>ProfileId!=Guid.Empty && ExpectedUserId>0 && AuthorizationGeneration>0 && PlaceId>0;
    public RobloxHandoffProbeContext Validated()
    {
        if(!IsValid) throw new ArgumentException("A handoff diagnostic requires a saved profile, positive user, authorization generation and place ID.");
        return this;
    }
    public override string ToString()=>"RobloxHandoffProbeContext { Profile = [redacted], Identity = [redacted], Generation = [redacted], Destination = [redacted] }";
}

public enum RobloxHandoffProbeOutcome
{
    CapturedAndPrevented,ContextRejected,OriginRejected,ExternalSchemeBlocked,MalformedHandoff,TimedOut,Cancelled,
    BrowserUnavailable,Failed,SignInRequired,IdentityMismatch,AuthorizationChanged,Busy,CleanupFailed
}

[Flags]
public enum RobloxHandoffKnownFields
{
    None=0,LaunchMode=1,GameInfo=2,LaunchTime=4,PlaceLauncherUrl=8,BrowserTrackerId=16,
    RobloxLocale=32,GameLocale=64,Channel=128
}

/// <summary>
/// Only bounded counts and fixed labels. Counts include duplicate occurrences; no field values,
/// unknown field names, origins, destination interpretation, ticket validity or TTL are retained.
/// Known labels are source-derived research vocabulary, not a current Roblox protocol specification.
/// </summary>
public sealed class RobloxHandoffProbeShape
{
    public int CharacterCount {get;}
    public int FieldCount {get;}
    public RobloxHandoffKnownFields KnownFields {get;}
    public int UnknownFieldCount {get;}
    public int DuplicateFieldCount {get;}
    internal RobloxHandoffProbeShape(int characters,int fields,RobloxHandoffKnownFields known,int unknown,int duplicates)
    {
        CharacterCount=characters;FieldCount=fields;KnownFields=known;UnknownFieldCount=unknown;DuplicateFieldCount=duplicates;
    }
    public override string ToString()=>$"RobloxHandoffProbeShape {{ Characters = {CharacterCount}, Fields = {FieldCount}, Known = {KnownFields}, Unknown = {UnknownFieldCount}, Duplicates = {DuplicateFieldCount} }}";
}

/// <summary>A credential-free diagnostic result. These constants never authorize native dispatch.</summary>
public sealed class RobloxHandoffProbeResult
{
    public RobloxHandoffProbeOutcome Outcome {get;}
    public RobloxHandoffProbeShape? Shape {get;}
    /// <summary>The SDK gesture flag only; it is not identity, consent, URI validity or launch authority.</summary>
    public bool? UserInitiated {get;}
    public bool CanLaunch=>false;
    public bool NativeLaunchPrevented=>true;
    public RobloxHandoffProbeResult(RobloxHandoffProbeOutcome outcome,RobloxHandoffProbeShape? shape=null,bool? userInitiated=null)
    {
        if(!Enum.IsDefined(outcome)) throw new ArgumentException("The handoff diagnostic outcome is invalid.");
        if(outcome==RobloxHandoffProbeOutcome.CapturedAndPrevented && shape is null)
            throw new ArgumentException("A captured handoff diagnostic requires a sanitized shape.");
        Outcome=outcome;Shape=shape;UserInitiated=userInitiated;
    }
    public override string ToString()=>$"RobloxHandoffProbeResult {{ Outcome = {Outcome}, CanLaunch = false, NativeLaunchPrevented = true }}";
}

/// <summary>
/// Owns only the returned current credential, never the caller's input credential. The browser
/// must cancel external dispatch, join cleanup and read its own current cookie before publication.
/// The service must verify that credential's identity before generation-fenced persistence.
/// </summary>
public sealed class RobloxHandoffProbeCapture : IDisposable
{
    private RobloxSessionCredential? _updatedCredential;
    public RobloxHandoffProbeResult Result {get;}
    [JsonIgnore] public RobloxSessionCredential? UpdatedCredential=>Volatile.Read(ref _updatedCredential);
    public RobloxHandoffProbeCapture(RobloxHandoffProbeResult result,RobloxSessionCredential? updatedCredential=null)
    {ArgumentNullException.ThrowIfNull(result);Result=result;_updatedCredential=updatedCredential;}
    public void Dispose()=>Interlocked.Exchange(ref _updatedCredential,null)?.Dispose();
    public override string ToString()=>$"RobloxHandoffProbeCapture {{ Outcome = {Result.Outcome}, Credential = [protected] }}";
}

public interface IRobloxHandoffProbe : IAsyncDisposable
{
    // One owned capture-only browser operation. Cancellation/disposal must join actual cleanup;
    // never return or persist the emitted URI. The caller continues to own input credential.
    Task<RobloxHandoffProbeCapture> InspectAsync(RobloxHandoffProbeContext context,RobloxSessionCredential credential,CancellationToken ct);
    // Why this probe would refuse right now without opening anything (Busy, CleanupFailed or
    // Cancelled), or null when it could start. Callers read it before reserving the grant, so a
    // refused check never costs the account its saved sign-in.
    RobloxHandoffProbeOutcome? Refusal=>null;
}

public enum RobloxHandoffProbeNavigation { Block,AllowGamePage,AllowCancelledRobloxPlayer }

/// <summary>
/// Pure bounded capture policy only. It neither loads a page nor cancels a native event itself.
/// The host must install an unconditional cancelling external-scheme handler before permitting
/// the diagnostic-only navigation exception, and cancel synchronously before calling InspectExternal.
/// Origin, context and shape are not authenticated account/native-arrival proof.
/// </summary>
public static class RobloxHandoffProbePolicy
{
    public const string OfficialOrigin="https://www.roblox.com";
    public const int MaximumUriCharacters=32*1024,MaximumUriUtf8Bytes=32*1024,MaximumFields=64,MaximumFieldNameCharacters=64;
    public const int MaximumPageCharacters=2048,MaximumSlugCharacters=256;
    private const string NativePrefix="roblox-player:";
    private const string GamePrefix="/games/";

    public static string CreateGamePageUri(RobloxHandoffProbeContext context)
    {ArgumentNullException.ThrowIfNull(context);return OfficialOrigin+GamePrefix+context.Validated().PlaceId.ToString(CultureInfo.InvariantCulture);}

    public static bool IsAllowedOrigin(string? origin)=>origin is not null
        && (origin.Equals(OfficialOrigin,StringComparison.OrdinalIgnoreCase) || origin.Equals(OfficialOrigin+"/",StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowedGamePage(RobloxHandoffProbeContext? context,string? address)
    {
        if(context is not {IsValid:true} || address is null || address.Length>MaximumPageCharacters
            || !address.AsSpan().StartsWith(OfficialOrigin,StringComparison.OrdinalIgnoreCase)) return false;
        var path=address.AsSpan(OfficialOrigin.Length);
        if(!path.StartsWith(GamePrefix,StringComparison.Ordinal) || !SafeText(address.AsSpan(),MaximumPageCharacters,MaximumUriUtf8Bytes)) return false;
        path=path[GamePrefix.Length..];int slash=path.IndexOf('/');var id=slash<0?path:path[..slash];
        Span<char> expected=stackalloc char[20];
        if(!context.PlaceId.TryFormat(expected,out int written,provider:CultureInfo.InvariantCulture) || !id.SequenceEqual(expected[..written])) return false;
        if(slash<0) return true;
        return SafeSlug(path[(slash+1)..]);
    }

    public static RobloxHandoffProbeNavigation ClassifyNavigation(RobloxHandoffProbeContext? context,string? address,bool cancellingExternalHandlerInstalled)
    {
        if(context is not {IsValid:true}) return RobloxHandoffProbeNavigation.Block;
        if(IsAllowedGamePage(context,address)) return RobloxHandoffProbeNavigation.AllowGamePage;
        if(cancellingExternalHandlerInstalled && NativeCandidate(address) && SafeText(address.AsSpan(),MaximumUriCharacters,MaximumUriUtf8Bytes))
            return RobloxHandoffProbeNavigation.AllowCancelledRobloxPlayer;
        return RobloxHandoffProbeNavigation.Block;
    }

    public static RobloxHandoffProbeResult InspectExternal(RobloxHandoffProbeContext? context,string? currentGamePageUri,string? initiatingOrigin,string? emittedUri)
    {
        if(context is not {IsValid:true} || !IsAllowedGamePage(context,currentGamePageUri)) return new(RobloxHandoffProbeOutcome.ContextRejected);
        if(!IsAllowedOrigin(initiatingOrigin)) return new(RobloxHandoffProbeOutcome.OriginRejected);
        if(emittedUri is null || emittedUri.Length>MaximumUriCharacters) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        if(!NativeCandidate(emittedUri)) return new(RobloxHandoffProbeOutcome.ExternalSchemeBlocked);
        if(!SafeText(emittedUri.AsSpan(),MaximumUriCharacters,MaximumUriUtf8Bytes)) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        return InspectShape(emittedUri.AsSpan());
    }

    private static bool NativeCandidate(string? address)=>address is {Length:>0 and <=MaximumUriCharacters}
        && address.Length>NativePrefix.Length && address.AsSpan().StartsWith(NativePrefix,StringComparison.OrdinalIgnoreCase);

    private static RobloxHandoffProbeResult InspectShape(ReadOnlySpan<char> uri)
    {
        var body=uri[NativePrefix.Length..];int plus=body.IndexOf('+');
        if(plus is <1 or >4) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        // Record no protocol-version value. A small numeric preamble is structural vocabulary
        // only, not a claim that this or any future native version is valid or supported.
        foreach(char digit in body[..plus]) if(!char.IsAsciiDigit(digit)) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        int offset=NativePrefix.Length+plus+1,fields=0,unknown=0,duplicates=0;
        var known=RobloxHandoffKnownFields.None;
        Span<int> starts=stackalloc int[MaximumFields];Span<int> lengths=stackalloc int[MaximumFields];
        while(offset<uri.Length)
        {
            if(fields>=MaximumFields) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
            var remaining=uri[offset..];int next=remaining.IndexOf('+');var field=next<0?remaining:remaining[..next];
            int colon=field.IndexOf(':');
            if(colon is <1 or >MaximumFieldNameCharacters) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
            var name=field[..colon];
            foreach(char character in name) if(!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
            bool repeated=false;
            for(int previous=0;previous<fields;previous++)
                if(name.Equals(uri.Slice(starts[previous],lengths[previous]),StringComparison.OrdinalIgnoreCase)) {repeated=true;break;}
            if(repeated) duplicates++;
            starts[fields]=offset;lengths[fields]=colon;fields++;
            var label=Known(name);known|=label;if(label==RobloxHandoffKnownFields.None) unknown++;
            if(next<0) {offset=uri.Length;break;}
            offset+=next+1;
            if(offset==uri.Length) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        }
        if(fields==0) return new(RobloxHandoffProbeOutcome.MalformedHandoff);
        var shape=new RobloxHandoffProbeShape(uri.Length,fields,known,unknown,duplicates);
        return new(duplicates>0?RobloxHandoffProbeOutcome.MalformedHandoff:RobloxHandoffProbeOutcome.CapturedAndPrevented,shape);
    }

    private static RobloxHandoffKnownFields Known(ReadOnlySpan<char> key)
    {
        if(key.Equals("launchmode",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.LaunchMode;
        if(key.Equals("gameinfo",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.GameInfo;
        if(key.Equals("launchtime",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.LaunchTime;
        if(key.Equals("placelauncherurl",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.PlaceLauncherUrl;
        if(key.Equals("browsertrackerid",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.BrowserTrackerId;
        if(key.Equals("robloxLocale",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.RobloxLocale;
        if(key.Equals("gameLocale",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.GameLocale;
        if(key.Equals("channel",StringComparison.OrdinalIgnoreCase)) return RobloxHandoffKnownFields.Channel;
        return RobloxHandoffKnownFields.None;
    }

    private static bool SafeSlug(ReadOnlySpan<char> slug)
    {
        if(slug.Length is 0 or >MaximumSlugCharacters || slug.SequenceEqual(".") || slug.SequenceEqual("..")) return false;
        for(int index=0;index<slug.Length;index++)
        {
            char character=slug[index];
            if(character=='%')
            {
                byte first=(byte)((Hex(slug[index+1])<<4)|Hex(slug[index+2])); // SafeText already checked all escapes.
                if(first<128 && (!Unreserved((char)first) || first=='.')) return false;
                int width=Utf8Width(first);index+=width*3-1;
            }
            else if(character<128 && !Unreserved(character)) return false;
        }
        return true;
    }
    private static bool Unreserved(char value)=>char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or '~';

    // Validate without unescaping/copying the secret URI or allocating unknown-name strings.
    // Escaped runes are decoded into at most4 stack bytes, checked, then discarded.
    private static bool SafeText(ReadOnlySpan<char> value,int maximumCharacters,int maximumBytes)
    {
        if(value.Length==0 || value.Length>maximumCharacters) return false;
        int bytes=0;Span<byte> encoded=stackalloc byte[4];
        for(int index=0;index<value.Length;index++)
        {
            char character=value[index];
            if(character=='%')
            {
                if(index+2>=value.Length || Hex(value[index+1])<0 || Hex(value[index+2])<0) return false;
                byte first=(byte)((Hex(value[index+1])<<4)|Hex(value[index+2]));int width=Utf8Width(first);
                if(width==0 || index+width*3>value.Length) return false;
                for(int item=0;item<width;item++)
                {
                    int at=index+item*3;
                    if(value[at]!='%' || Hex(value[at+1])<0 || Hex(value[at+2])<0) return false;
                    encoded[item]=(byte)((Hex(value[at+1])<<4)|Hex(value[at+2]));
                }
                if(Rune.DecodeFromUtf8(encoded[..width],out var escaped,out int consumed)!=OperationStatus.Done || consumed!=width || Forbidden(escaped)) return false;
                bytes+=width*3;index+=width*3-1;
            }
            else
            {
                if(char.IsWhiteSpace(character)) return false;
                Rune rune;
                if(char.IsHighSurrogate(character))
                {if(index+1>=value.Length || !Rune.TryCreate(character,value[index+1],out rune)) return false;index++;}
                else if(!Rune.TryCreate(character,out rune)) return false;
                if(Forbidden(rune)) return false;bytes+=rune.Utf8SequenceLength;
            }
            if(bytes>maximumBytes) return false;
        }
        return true;
    }
    private static bool Forbidden(Rune rune)=>Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    private static int Utf8Width(byte first)=>first<0x80?1:first is >=0xc2 and <=0xdf?2:first is >=0xe0 and <=0xef?3:first is >=0xf0 and <=0xf4?4:0;
    private static int Hex(char value)=>value is >='0' and <='9'?value-'0':value is >='a' and <='f'?value-'a'+10:value is >='A' and <='F'?value-'A'+10:-1;
}
