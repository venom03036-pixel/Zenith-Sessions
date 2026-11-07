using System.IO;
using System.Text;
using System.Text.Json;

namespace Zenith.Sessions.Core;

/// <summary>Only portable manager cadence/pacing values. Account selection and safety admission are local settings.</summary>
public sealed record PortablePerformanceSettings(ManagerPerformanceProfile Profile=ManagerPerformanceProfile.Balanced,
    int CustomProcessSampleMs=1000,int CustomPresentationMs=1000,int CustomStaggerMs=2000,int CustomStabilizationMs=20000)
{
    public PortablePerformanceSettings Validated()
    {
        ManagerPerformanceValidation.ValidateProfile(Profile);
        ManagerPerformanceValidation.ValidateTiming(CustomProcessSampleMs,CustomPresentationMs,CustomStaggerMs,CustomStabilizationMs);
        return this;
    }
    public static PortablePerformanceSettings FromSettings(ManagerPerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);settings.Validated();
        return new(settings.Profile,settings.CustomProcessSampleMs,settings.CustomPresentationMs,settings.CustomStaggerMs,settings.CustomStabilizationMs);
    }
    /// <summary>Creates a detached review candidate. It never applies or saves anything; the main account comes only from the supplied local settings.</summary>
    public ManagerPerformanceSettings ToReviewSettings(ManagerPerformanceSettings localSettings)
    {
        ArgumentNullException.ThrowIfNull(localSettings);localSettings.Validated();Validated();
        return new(Profile,localSettings.MainAccountProfileId,CustomProcessSampleMs,CustomPresentationMs,CustomStaggerMs,CustomStabilizationMs);
    }
}

public enum PerformanceSettingsTransferProblem { InvalidData, UnsupportedVersion, TooLarge }
public sealed class PerformanceSettingsTransferException(PerformanceSettingsTransferProblem problem) : Exception(problem switch
{
    PerformanceSettingsTransferProblem.UnsupportedVersion=>"This performance transfer uses an unsupported version. No settings were changed.",
    PerformanceSettingsTransferProblem.TooLarge=>"This performance transfer exceeds its size limit. No settings were changed.",
    _=>"This is not a valid manager performance transfer. No settings were changed."
})
{
    public PerformanceSettingsTransferProblem Problem {get;}=problem;
}

/// <summary>Strict, bounded, secret-free serialization. Base64 is transfer encoding, not encryption or authentication.</summary>
public static class PerformanceSettingsTransfer
{
    public const int MaximumJsonBytes=4096;
    public const string ShareCodePrefix="ZSPERF1:";
    public const int MaximumShareCodeLength=8+((MaximumJsonBytes+2)/3)*4;
    public const string ShareCodeNotice="Share codes use Base64 for transfer. They are not encrypted or authenticated.";
    private const string Format="zenith-manager-performance";
    private static readonly UTF8Encoding StrictUtf8=new(false,true);

    public static byte[] SerializeJson(PortablePerformanceSettings settings,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(settings);
        try {settings.Validated();}
        catch(ArgumentException) {throw Invalid();}
        using var output=new MemoryStream(384);
        using(var json=new Utf8JsonWriter(output))
        {
            json.WriteStartObject();json.WriteString("format",Format);json.WriteNumber("version",1);
            json.WriteString("profile",settings.Profile.ToString());
            json.WriteNumber("customProcessSampleMs",settings.CustomProcessSampleMs);
            json.WriteNumber("customPresentationMs",settings.CustomPresentationMs);
            json.WriteNumber("customStaggerMs",settings.CustomStaggerMs);
            json.WriteNumber("customStabilizationMs",settings.CustomStabilizationMs);json.WriteEndObject();
        }
        ct.ThrowIfCancellationRequested();return output.ToArray();
    }
    public static PortablePerformanceSettings ParseJson(ReadOnlyMemory<byte> bytes,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(bytes.Length>MaximumJsonBytes) throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.TooLarge);
        try
        {
            StrictUtf8.GetCharCount(bytes.Span);
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=2});var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object) throw Invalid();
            int seen=0;
            foreach(var property in root.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();int field=property.Name switch
                {
                    "format"=>1,"version"=>2,"profile"=>4,"customProcessSampleMs"=>8,
                    "customPresentationMs"=>16,"customStaggerMs"=>32,"customStabilizationMs"=>64,_=>0
                };
                if(field==0 || (seen&field)!=0) throw Invalid();seen|=field;
            }
            if(seen!=127 || root.GetProperty("format").GetString()!=Format) throw Invalid();
            if(root.GetProperty("version").GetInt32()!=1) throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.UnsupportedVersion);
            string? name=root.GetProperty("profile").GetString();
            if(name is null || !Enum.TryParse<ManagerPerformanceProfile>(name,out var profile) || !Enum.IsDefined(profile) || profile.ToString()!=name) throw Invalid();
            var settings=new PortablePerformanceSettings(profile,root.GetProperty("customProcessSampleMs").GetInt32(),
                root.GetProperty("customPresentationMs").GetInt32(),root.GetProperty("customStaggerMs").GetInt32(),root.GetProperty("customStabilizationMs").GetInt32());
            settings.Validated();ct.ThrowIfCancellationRequested();return settings;
        }
        catch(Exception ex) when(ex is JsonException or DecoderFallbackException or FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        {throw Invalid();}
    }
    /// <summary>Reads at most 4097 bytes to detect an oversized transfer. The caller keeps ownership of the stream.</summary>
    public static async Task<PortablePerformanceSettings> ReadJsonAsync(Stream source,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(source);var buffer=new byte[MaximumJsonBytes+1];int used=0;
        while(true)
        {
            int read=await source.ReadAsync(buffer.AsMemory(used),ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();
            if(read==0) return ParseJson(buffer.AsMemory(0,used),ct);
            used+=read;if(used>MaximumJsonBytes) throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.TooLarge);
        }
    }
    public static string EncodeShareCode(PortablePerformanceSettings settings,CancellationToken ct=default)
        =>ShareCodePrefix+Convert.ToBase64String(SerializeJson(settings,ct));
    public static PortablePerformanceSettings DecodeShareCode(string code,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(code is null) throw Invalid();
        if(code.Length>MaximumShareCodeLength) throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.TooLarge);
        if(!code.StartsWith(ShareCodePrefix,StringComparison.Ordinal))
        {
            if(code.StartsWith("ZSPERF",StringComparison.Ordinal) && code.IndexOf(':',6) is >=7 and <=12)
                throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.UnsupportedVersion);
            throw Invalid();
        }
        var payload=code.AsSpan(ShareCodePrefix.Length);
        if(payload.IsEmpty) throw Invalid();
        foreach(char character in payload) if(char.IsWhiteSpace(character)) throw Invalid();
        var bytes=new byte[MaximumJsonBytes];
        if(!Convert.TryFromBase64Chars(payload,bytes,out int length) || !Convert.ToBase64String(bytes,0,length).AsSpan().SequenceEqual(payload)) throw Invalid();
        return ParseJson(bytes.AsMemory(0,length),ct);
    }
    private static PerformanceSettingsTransferException Invalid()=>new(PerformanceSettingsTransferProblem.InvalidData);
}

public interface IPerformanceSettingsTransferStore
{
    Task<PortablePerformanceSettings> ImportAsync(string path,CancellationToken ct);
    Task ExportAsync(string path,PortablePerformanceSettings settings,CancellationToken ct);
}
