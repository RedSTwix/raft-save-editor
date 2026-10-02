using System.Diagnostics;
using System.IO;
using System.Reflection;
using SharpMonoInjector;

namespace RaftSaveEditor.App.Services;

internal sealed class LiveGameInjector(LiveGameClient client)
{
    internal async Task EnsureConnectedAsync(Process process)
    {
        try
        {
            await client.PingAsync();
            return;
        }
        catch (LiveConnectorException)
        {
            // The pipe is absent: inject only after Raft has already initialized normally.
        }

        if (process.HasExited)
            throw new LiveConnectorException("O Raft foi fechado antes da conexão.");

        byte[] connector = ReadEmbeddedConnector();
        try
        {
            await Task.Run(() =>
            {
                using Injector injector = new(process.Id);
                injector.Inject(connector, "RaftSaveEditor.Connector", "EntryPoint", "Initialize");
            });
        }
        catch (Exception ex)
        {
            string detail = ex.GetBaseException().Message;
            throw new LiveConnectorException($"Não foi possível ativar o conector temporário no Raft: {detail}");
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250);
            try
            {
                await client.PingAsync();
                return;
            }
            catch (LiveConnectorException) when (attempt < 19)
            {
            }
        }
        throw new LiveConnectorException("O conector foi carregado, mas o jogo não abriu o canal local.");
    }

    private static byte[] ReadEmbeddedConnector()
    {
        Assembly assembly = typeof(LiveGameInjector).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("RaftSaveEditor.Connector.dll");
        if (stream is null)
            throw new LiveConnectorException("O conector interno não foi encontrado no executável.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
