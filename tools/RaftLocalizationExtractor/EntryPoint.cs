using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RaftSaveEditor.LocalizationExtractor;

public static class EntryPoint
{
    public static void Export()
    {
        try
        {
            string outputPath = Path.Combine(Path.GetTempPath(), "RaftSaveEditor.items.localized.txt");
            List<Item_Base> items = ItemManager.GetAllItems()
                .Where(item => item != null)
                .OrderBy(item => item.UniqueIndex)
                .ToList();

            using StreamWriter writer = new(outputPath, false, new UTF8Encoding(false));
            writer.WriteLine($"# Language={Escape(I2.Loc.LocalizationManager.CurrentLanguage)}");
            writer.WriteLine("# ID|DisplayName|UniqueName|LocalizationTerm");
            foreach (Item_Base item in items)
            {
                ItemInstance_Inventory inventory = item.settings_Inventory;
                writer.WriteLine($"{item.UniqueIndex}|{Escape(inventory?.DisplayName)}|{Escape(item.UniqueName)}|{Escape(inventory?.LocalizationTerm)}");
            }
            Debug.Log($"[Raft Save Editor] {items.Count} nomes oficiais exportados para {outputPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Raft Save Editor] Falha ao exportar nomes localizados: {ex}");
        }
    }

    private static string Escape(string value) => (value ?? string.Empty)
        .Replace("\\", "\\\\")
        .Replace("|", "\\p")
        .Replace("\r", string.Empty)
        .Replace("\n", "\\n");
}
