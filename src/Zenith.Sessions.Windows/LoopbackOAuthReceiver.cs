using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public interface IOAuthBrowserFlow
{ Task<Uri> AuthorizeAsync(OAuthAttempt attempt,CancellationToken ct); }

/// <summary>Only loopback sockets, bounded HTTP headers, two readers, no URL ACL or administrator privileges.</summary>
public sealed class LoopbackOAuthReceiver : IOAuthBrowserFlow
{
    public async Task<Uri> AuthorizeAsync(OAuthAttempt attempt,CancellationToken ct)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromMinutes(3));
        var callbacks=Channel.CreateBounded<Uri>(new BoundedChannelOptions(1) { SingleReader=true,SingleWriter=false });
        var listeners=new List<TcpListener>(); var tasks=new List<Task>();
        try
        {
            foreach(var address in new[] { IPAddress.Loopback,IPAddress.IPv6Loopback })
            {
                var listener=new TcpListener(address,attempt.Callback.Port);
                try { listener.Server.ExclusiveAddressUse=true; listener.Start(2); listeners.Add(listener); }
                catch(SocketException) when(address.Equals(IPAddress.IPv6Loopback) && listeners.Count>0) { listener.Stop(); }
            }
            foreach(var listener in listeners) tasks.Add(ReceiveAsync(listener,attempt,callbacks.Writer,stop.Token));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName=attempt.AuthorizeUri.AbsoluteUri,UseShellExecute=true });
            try { return await callbacks.Reader.ReadAsync(stop.Token); }
            catch(ChannelClosedException ex) when(ex.InnerException is AccountLinkException failure)
            { throw new AccountLinkException(failure.Message); }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            { throw new AccountLinkException("Sign-in timed out. Check your browser and registered localhost callback, then try again."); }
        }
        catch(SocketException) { throw new AccountLinkException("The local sign-in callback port is in use. Close the other sign-in attempt or change the callback port in Settings."); }
        finally
        {
            stop.Cancel(); foreach(var listener in listeners) listener.Stop();
            await Task.WhenAll(tasks);
        }
    }
    private static async Task ReceiveAsync(TcpListener listener,OAuthAttempt attempt,ChannelWriter<Uri> output,CancellationToken ct)
    {
        try
        {
            for(int requests=0;requests<32 && !ct.IsCancellationRequested;requests++)
            {
                using var peer=await listener.AcceptTcpClientAsync(ct);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await using var stream=peer.GetStream();
                    byte[] buffer=new byte[16384]; int count=0,end=-1;
                    while(count<buffer.Length && end<0)
                    {
                        int read=await stream.ReadAsync(buffer.AsMemory(count),deadline.Token); if(read==0) break; count+=read;
                        end=buffer.AsSpan(0,count).IndexOf("\r\n\r\n"u8);
                    }
                    Uri? callback=end>=0?ParseRequest(buffer.AsSpan(0,end),attempt):null;
                    string body=callback is null?"This callback did not match the current sign-in. Return to Zenith Sessions and try again.":"Sign-in received. Return to Zenith Sessions; the account will be verified before it is linked.";
                    byte[] reply=Encoding.UTF8.GetBytes($"HTTP/1.1 {(callback is null?"400 Bad Request":"200 OK")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(reply,deadline.Token);
                    if(callback is not null) { output.TryWrite(callback); return; }
                }
                catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException) { if(ct.IsCancellationRequested) return; }
            }
            if(!ct.IsCancellationRequested) output.TryComplete(new AccountLinkException("Too many unrelated local sign-in callbacks. Cancel and start sign-in again."));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(SocketException) when(ct.IsCancellationRequested) { }
        catch(ObjectDisposedException) when(ct.IsCancellationRequested) { }
        catch(SocketException) { output.TryComplete(new AccountLinkException("The local sign-in listener failed. Close other sign-in attempts and retry.")); }
    }
    internal static Uri? ParseRequest(ReadOnlySpan<byte> bytes,OAuthAttempt attempt)
    {
        foreach(byte value in bytes) if(value>127 || value==0) return null;
        string request=Encoding.ASCII.GetString(bytes);
        if(request.Any(c=>c>127 || c=='\0')) return null;
        var lines=request.Split("\r\n",StringSplitOptions.None); if(lines.Length>64) return null;
        var first=lines[0].Split(' ');
        if(first.Length!=3 || first[0]!="GET" || first[2]!="HTTP/1.1" || !first[1].StartsWith("/zenith/oauth/?",StringComparison.Ordinal)) return null;
        int hosts=0;
        foreach(string line in lines.Skip(1))
        {
            int colon=line.IndexOf(':'); if(colon<=0) return null;
            string name=line[..colon],value=line[(colon+1)..].Trim();
            if(name.Equals("Host",StringComparison.OrdinalIgnoreCase)) { if(value!=$"localhost:{attempt.Callback.Port}" || ++hosts!=1) return null; }
            if(name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase) && value!="0") return null;
        }
        if(hosts!=1 || !Uri.TryCreate($"http://localhost:{attempt.Callback.Port}"+first[1],UriKind.Absolute,out var uri)) return null;
        try { attempt.ValidateCallback(uri); return uri; }
        catch(AccountLinkException) { return null; }
    }
}
