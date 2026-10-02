# Raft Save Editor

Aplicativo desktop WPF v1.0 para editar itens dos saves `.rgd` do Raft e, opcionalmente, inventários do jogo aberto.

## Estado atual

- detecta automaticamente os saves na pasta padrão do Raft, permite seleção interna e aceita drag-and-drop de `.rgd`;
- valida e percorre o payload NRBF sem `BinaryFormatter.Deserialize()`;
- encontra `RGD_Slot` e lê `SlotIndex`, `ItemIndex`, `ItemAmount` e `ItemUses`;
- identifica storages e inventários/hotbar quando essa relação existe no grafo;
- pesquisa local por nome ou ID, filtros por categoria, favoritos e itens recentes;
- detecta storages, Player Storage, hotbar, inventário interno e mochila;
- maximiza, organiza por categoria e remove itens inválidos em lote;
- inclui presets de construção, comida/água, ferramentas e combate, além de presets personalizados;
- mostra totais consolidados e todas as ocorrências de cada item;
- mantém alterações em memória até a confirmação;
- permite exportar manualmente uma cópia ZIP do save aberto;
- revalida o payload inteiro e compara o diff byte a byte antes de substituir o arquivo;
- salva diretamente no arquivo selecionado, sem criar cópias de backup;
- oferece modo ao vivo com bloqueio durante carregamento/save e verificação do item antes de alterar o slot;
- possui uma tela de diagnóstico com versão, caminhos, contagens, estado do jogo/conector e último erro.

## Modos de edição

- **OFFLINE:** as mudanças ficam pendentes e só entram no `.rgd` após `Salvar alterações`.
- **AO VIVO:** exige o Raft aberto e um mundo totalmente carregado. O conector é temporário e atua somente no processo local do jogo.

O editor não cria backup automático. O botão `Exportar cópia` cria uma cópia manual quando você desejar.

`ItemAmount` é realmente `System.Int16` nos saves analisados. A faixa aceita é, portanto, `0..32767`; `30000` é válido.

## Compilar e executar

Requer Windows e .NET 8 SDK.

Para gerar um executável self-contained na raiz do projeto, execute:

```text
build.bat
```

O resultado será `RaftSaveEditor.exe` na raiz. Os arquivos intermediários da publicação ficam em `publish`.

O build detecta a instalação do jogo pelo processo aberto, bibliotecas Steam e pastas Downloads/Desktop. Se houver mais de uma instalação, defina `RAFT_GAME_DIR` com a pasta que contém `Raft.exe`. O build usa DLLs locais do Raft apenas como referências de compilação; elas não são incluídas no repositório. Nenhum loader é instalado na pasta do jogo.

```powershell
dotnet build RaftSaveEditor.sln
dotnet run --project .\src\RaftSaveEditor.App\RaftSaveEditor.App.csproj
```

Também é possível passar um save na linha de comando:

```powershell
dotnet run --project .\src\RaftSaveEditor.App\RaftSaveEditor.App.csproj -- "C:\caminho\save.rgd"
```

Para publicar uma pasta executável dependente do runtime:

```powershell
dotnet publish .\src\RaftSaveEditor.App\RaftSaveEditor.App.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

## Testes

```powershell
dotnet test RaftSaveEditor.sln
```

Um teste de integração opcional abre e edita apenas uma cópia temporária de um save real:

```powershell
$env:RAFT_TEST_SAVE = "C:\caminho\save.rgd"
dotnet test .\tests\RaftSaveEditor.Tests\RaftSaveEditor.Tests.csproj
```

## Catálogo de itens

O catálogo versionado fica em `src/RaftSaveEditor.Core/Catalog/items.metadata.txt` e guarda ID, nome usado pelo jogo, StackSize, Uses máximos, tipo, ícone e disponibilidade em inventários. IDs não catalogados aparecem como `Unknown Item (ID N)` e nunca são descartados automaticamente.

Para atualizar nomes sem recompilar, coloque `items.override.txt` ao lado do executável usando uma entrada `ID|Nome` por linha. O arquivo é mesclado sobre o catálogo embutido.

O catálogo fica isolado do parser para poder ser atualizado sem tocar no código NRBF.

## Segurança

O projeto usa [`System.Formats.Nrbf`](https://www.nuget.org/packages/System.Formats.Nrbf) como decodificador oficial e somente leitura. A documentação da Microsoft descreve essa API como um leitor de records, não um desserializador de objetos: [ler payloads NRBF com segurança](https://learn.microsoft.com/dotnet/standard/serialization/binaryformatter-migration-guide/read-nrbf-payloads).

O escritor não recria o grafo e não carrega tipos de `Assembly-CSharp`. Ele faz uma segunda caminhada estruturada pelos records NRBF para obter offsets de membros primitivos, exige que os quatro campos do `RGD_Slot` sejam `Int16`, altera apenas os offsets validados de `ItemAmount` e decodifica novamente o resultado antes da gravação atômica.

Logs técnicos ficam em `%LOCALAPPDATA%\RaftSaveEditor\raft-save-editor.log`; bytes do save não são registrados.
