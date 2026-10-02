# Formato observado nos saves reais

Arquivos analisados localmente:

- save mundial: raiz `RGD_Game`, 5.288 records com ID e 219 `RGD_Slot`;
- save de usuários: raiz `RGD_Users`, 80 records com ID, 4 `RGD_PlayerInventory` e 48 `RGD_Slot`.

Schema observado de `RGD_Slot`:

| Membro | Tipo NRBF/CLR |
|---|---|
| `SlotIndex` | `System.Int16` |
| `ItemIndex` | `System.Int16` |
| `ItemAmount` | `System.Int16` |
| `ItemUses` | `System.Int16` |
| `ExclusiveString` | `System.String` ou `null` |

Os slots de storage aparecem em arrays `RGD_Slot[]` pertencentes ao membro `RGD_Storage.Slots`. Os slots de jogador aparecem em `RGD_PlayerInventory.Slots`; índices `0..9` são apresentados como hotbar e os demais como inventário. Quando a cadeia proprietária não é demonstrável pelo record pai, a interface usa `Unknown Container` e preserva o identificador técnico.

## Estratégia de escrita

1. `System.Formats.Nrbf.NrbfDecoder` valida todo o payload sem instanciar tipos arbitrários.
2. `NrbfOffsetParser` percorre a mesma gramática de records e registra o offset exato de cada primitivo.
3. Os slots encontrados pelos dois leitores são comparados como multiconjunto de valores.
4. Cada edição troca somente os dois bytes little-endian de `ItemAmount`.
5. Um diff completo garante que nenhum outro byte mudou.
6. O payload alterado é decodificado e rastreado outra vez.
7. Um arquivo temporário validado substitui o original diretamente, sem criar backup.

Não há busca hexadecimal, regex binária, `BinaryFormatter.Deserialize`, binder de tipos ou reconstrução de objetos desconhecidos.
