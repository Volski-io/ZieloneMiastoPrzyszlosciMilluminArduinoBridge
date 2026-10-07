using System.Globalization;
using System.Text;

namespace Bridge.Core.ModelProtocol;

public static class ModelCommandCatalog
{
    private static readonly IReadOnlyList<ModelCommandDefinition> Definitions = CreateDefinitions();
    private static readonly IReadOnlyDictionary<byte, ModelCommandDefinition> ByData =
        Definitions.ToDictionary(item => item.Data);
    private static readonly IReadOnlyDictionary<string, ModelCommandDefinition> ByOscAddress =
        Definitions
            .Where(item => !item.IsReserved && item.IsSendable)
            .ToDictionary(item => item.OscAddress, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModelCommandDefinition> All => Definitions;

    public static ModelCommandDefinition Get(byte data) => ByData[data];

    public static bool TryGetByOscAddress(string address, out ModelCommandDefinition command) =>
        ByOscAddress.TryGetValue(address, out command!);

    private static IReadOnlyList<ModelCommandDefinition> CreateDefinitions()
    {
        var commands = Enumerable.Range(0, 128)
            .Select(value => new ModelCommandDefinition((byte)value, "REZERWA", Category(value), IsReserved: true))
            .ToArray();

        Set(commands, 0x00, "Brak", "System", wire: "RS");
        Set(commands, 0x01, "ACK failed", "System", note: "Ostatni ACK różni się od oczekiwanej wartości.");
        Set(commands, 0x02, "Brak scenariusza / włączenie przycisków", "Status");
        Set(commands, 0x03, "Scenariusz w trakcie / wyłączenie przycisków", "Status");

        for (var fan = 1; fan <= 5; fan++)
        {
            var off = (byte)(0x04 + ((fan - 1) * 2));
            Set(commands, off, $"Wiatrak {fan} off", "Wiatraki", wire: ((char)('A' + fan - 1)).ToString().PadLeft(2, 'A'));
            Set(commands, (byte)(off + 1), $"Wiatrak {fan}: ustaw prędkość aktywną", "Wiatraki");
        }

        Set(commands, 0x0E, "LED-y wiatrak off", "Wiatraki", wire: "AF");
        Set(commands, 0x0F, "LED-y wiatrak on", "Wiatraki");

        for (var level = 1; level <= 10; level++)
        {
            Set(commands, (byte)(0x0F + level), $"Aktywny element {level * 10}%", "Wartość aktywna");
        }

        for (var balloon = 1; balloon <= 3; balloon++)
        {
            var off = (byte)(0x1A + ((balloon - 1) * 2));
            Set(commands, off, $"Balon {balloon} off", "Balony", wire: $"A{(char)('F' + balloon)}");
            Set(commands, (byte)(off + 1), $"Balon {balloon}: ustaw prędkość aktywną", "Balony");
        }

        const string sectorSelectionNote =
            "Komenda ON stosuje globalny wybór z 0x50-0x59; pusty wybór wyłącza wszystkie sektory.";

        Set(commands, 0x20, "Słupy HV off", "Słupy i sektory");
        Set(commands, 0x21, "Słupy HV: zastosuj wybór sektorów [1-6]", "Słupy i sektory", wire: "S1-S6", note: sectorSelectionNote);
        Set(commands, 0x22, "LED-y na balonach off", "Balony", wire: "EE EF EG");
        Set(commands, 0x23, "LED-y na balonach: zastosuj wybór sektorów [1-3]", "Balony", note: sectorSelectionNote);
        Set(commands, 0x24, "InPost LED off", "Obiekty", wire: "INPOST");
        Set(commands, 0x25, "InPost LED on", "Obiekty", note: "W arkuszu opis 0x25 powtarzał 'off'; para adresów wskazuje funkcję on.");
        Set(commands, 0x26, "Planetarium: kopuła silnik off", "Planetarium", wire: "PL");
        Set(commands, 0x27, "Planetarium: kopuła silnik on", "Planetarium");
        Set(commands, 0x28, "Wyślij impuls na przycisk", "Planetarium", wire: "PR");
        Set(commands, 0x29, "Znacznik początku ramki", "System", note: "0x29 jest bajtem START i nie może być wysłane jako DATA.", sendable: false);

        Pair(commands, 0x2A, "Fabryka porcelany", "Obiekty", "AO");
        Pair(commands, 0x2C, "Beata Drzazga", "Obiekty", "AP");
        Pair(commands, 0x2E, "NOSPR", "Obiekty", "AR");
        Pair(commands, 0x30, "Mercedes salon", "Obiekty", "AS");
        Pair(commands, 0x32, "Muzeum Śląskie", "Obiekty", "AT");
        Pair(commands, 0x34, "Wieża szybu", "Obiekty", "AU");
        Pair(commands, 0x36, "Dworzec kolejowy", "Obiekty", "AW");
        Pair(commands, 0x38, "Planetarium LED", "Planetarium", "AX");
        Pair(commands, 0x3A, "Planetarium projektor", "Planetarium", "AY");
        Pair(commands, 0x3C, "Biurowiec 1", "Budynki", "AZ",
            "W arkuszu K62 widnieje 0xBC; układ bitów, kolejność i ACK 0xBC wskazują DATA=0x3C.");
        Pair(commands, 0x3E, "Biurowiec 2", "Budynki", "BB");
        Pair(commands, 0x40, "Elektrownia gazowa", "Obiekty", "BC");
        Pair(commands, 0x42, "Elka", "Obiekty", "BD");
        Pair(commands, 0x44, "Stopsig autobusu przegubowego", "Transport", "BE");
        Pair(commands, 0x46, "Przystanek autobusu", "Transport", "BF");
        Pair(commands, 0x48, "Przystanek Mercedesa", "Transport", "BG");
        Pair(commands, 0x4A, "Pociąg", "Transport", "BH");
        Pair(commands, 0x4C, "Chmura", "Obiekty", "BI");
        Pair(commands, 0x4E, "Chmura pompka", "Obiekty", "BJ");

        Set(commands, 0x50, "Wybierz wszystkie sektory", "Wybór sektorów",
            note: "Ustawia wszystkie sektory jako wybrane dla następnej komendy ON urządzenia sektorowego.");
        for (var sector = 1; sector <= 8; sector++)
        {
            Set(commands, (byte)(0x50 + sector), $"Przełącz wybór sektora {sector}", "Wybór sektorów",
                note: "Przełącza sektor w globalnym wyborze używanym przez kolejną komendę ON.");
        }

        Set(commands, 0x59, "Wyczyść wybór sektorów", "Wybór sektorów",
            note: "Odznacza wszystkie sektory; następna komenda ON wyłączy wszystkie obsługiwane sektory.");

        Set(commands, 0x5E, "Zabudowa mieszkaniowa 0 off", "Budynki", wire: "FF FG FH");
        Set(commands, 0x5F, "Zabudowa mieszkaniowa 0: zastosuj wybór sektorów [1-3]", "Budynki", note: sectorSelectionNote);
        Set(commands, 0x60, "Budynek 1 off", "Budynki", wire: "BK BL BM BN BO BP");
        Set(commands, 0x61, "Budynek 1: zastosuj wybór sektorów [1-6]", "Budynki", note: sectorSelectionNote);
        Set(commands, 0x62, "Budynek 2 off", "Budynki", wire: "BQ BR BS BT BU BW BX");
        Set(commands, 0x63, "Budynek 2: zastosuj wybór sektorów [1-7]", "Budynki", note: sectorSelectionNote);
        Set(commands, 0x64, "Budynek 3 off", "Budynki", wire: "BY BZ CC CE CF CG CH CI");
        Set(commands, 0x65, "Budynek 3: zastosuj wybór sektorów [1-8]", "Budynki", note: sectorSelectionNote);
        Set(commands, 0x66, "Hotel 1 off", "Budynki", wire: "CJ CK CL");
        Set(commands, 0x67, "Hotel 1: zastosuj wybór sektorów [1-3]", "Budynki", note: sectorSelectionNote);
        Pair(commands, 0x68, "LED Elka", "Obiekty", "PE");
        Set(commands, 0x6A, "Zabudowa mieszkaniowa 1 off", "Budynki", wire: "CP CQ CR");
        Set(commands, 0x6B, "Zabudowa mieszkaniowa 1: zastosuj wybór sektorów [1-3]", "Budynki", note: sectorSelectionNote);
        Pair(commands, 0x6C, "Elektrownia gazowa RGB", "RGB", "CS");
        Set(commands, 0x6E, "Farma fotowoltaiczna 1 RGB off", "RGB", wire: "CX CY CZ");
        Set(commands, 0x6F, "Farma fotowoltaiczna 1 RGB: zastosuj wybór sektorów [1-3]", "RGB", note: sectorSelectionNote);
        Set(commands, 0x72, "Magazyn energii RGB off", "RGB", wire: "DI DJ");
        Set(commands, 0x73, "Magazyn energii RGB: zastosuj wybór sektorów [1-2]", "RGB", note: sectorSelectionNote);
        Pair(commands, 0x74, "GPZ RGB", "RGB", "DM");
        Pair(commands, 0x76, "Elektrownia Złotniki", "Obiekty", "DQ");

        for (var scenario = 1; scenario <= 8; scenario++)
        {
            Set(commands, (byte)(0x77 + scenario), $"Wciśnięto scenariusz {scenario}", "Scenariusze", wire: ScenarioWire(scenario));
        }

        var all = new List<ModelCommandDefinition>(256);
        all.AddRange(commands);
        all.Add(new ModelCommandDefinition(
            0x80,
            "Checksum failed",
            "System",
            Note: "Kod błędnej sumy kontrolnej.",
            OscPath: "/makieta/system/checksum-failed"));
        all.Add(new ModelCommandDefinition(
            0x81,
            "REZERWA",
            "ACK",
            IsReserved: true,
            OscPath: "/makieta/ack/rezerwa"));

        for (var value = 0x82; value <= 0xFF; value++)
        {
            var source = commands[value & 0x7F];
            all.Add(new ModelCommandDefinition(
                (byte)value,
                $"ACK {source.DataHex}: {source.Name}",
                "ACK",
                Note: source.IsReserved ? "ACK przypisany do pozycji rezerwowej." : null,
                IsReserved: source.IsReserved,
                OscPath: $"/makieta/ack{source.OscAddress["/makieta".Length..]}"));
        }

        return all;
    }

    private static void Pair(
        ModelCommandDefinition[] commands,
        byte off,
        string name,
        string category,
        string wire,
        string? note = null)
    {
        Set(commands, off, $"{name} off", category, wire, note);
        Set(commands, (byte)(off + 1), $"{name} on", category);
    }

    private static void Set(
        ModelCommandDefinition[] commands,
        byte data,
        string name,
        string category,
        string? wire = null,
        string? note = null,
        bool sendable = true)
    {
        commands[data] = new ModelCommandDefinition(
            data,
            name,
            category,
            wire,
            note,
            IsSendable: sendable,
            OscPath: BuildOscAddress(data, name, category));
    }

    private static string BuildOscAddress(byte data, string name, string category) => data switch
    {
        0x00 => "/makieta/system/brak",
        0x01 => "/makieta/system/ack-failed",
        0x02 => "/makieta/status/brak-scenariusza",
        0x03 => "/makieta/status/scenariusz-w-trakcie",
        >= 0x04 and <= 0x0D =>
            $"/makieta/wiatrak/{((data - 0x04) / 2) + 1}/{(data % 2 == 0 ? "off" : "predkosc-aktywna")}",
        0x0E => "/makieta/wiatraki/led/off",
        0x0F => "/makieta/wiatraki/led/on",
        >= 0x10 and <= 0x19 => $"/makieta/wartosc-aktywna/{(data - 0x0F) * 10}",
        >= 0x1A and <= 0x1F =>
            $"/makieta/balon/{((data - 0x1A) / 2) + 1}/{(data % 2 == 0 ? "off" : "predkosc-aktywna")}",
        0x20 => "/makieta/slupy/off",
        0x21 => "/makieta/slupy/on",
        0x22 => "/makieta/balony/led/off",
        0x23 => "/makieta/balony/led/on",
        0x24 => "/makieta/inpost/led/off",
        0x25 => "/makieta/inpost/led/on",
        0x26 => "/makieta/planetarium/kopula/off",
        0x27 => "/makieta/planetarium/kopula/on",
        0x28 => "/makieta/planetarium/przycisk/impuls",
        0x29 => "/makieta/system/start-ramki",
        0x50 => "/makieta/sektory/wybierz-wszystkie",
        >= 0x51 and <= 0x58 => $"/makieta/sektory/{data - 0x50}/przelacz",
        0x59 => "/makieta/sektory/wyczysc",
        >= 0x78 and <= 0x7F => $"/makieta/scenariusz/{data - 0x77}/uruchom",
        _ => $"/makieta/{Slug(category)}/{Slug(name)}"
    };

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        var separatorPending = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0)
                {
                    result.Append('-');
                }
                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        return result.ToString();
    }

    private static string Category(int value) => value switch
    {
        <= 0x03 => "System",
        <= 0x0F => "Wiatraki",
        <= 0x19 => "Wartość aktywna",
        <= 0x1F => "Balony",
        <= 0x28 => "Obiekty",
        0x29 => "System",
        <= 0x4F => "Obiekty",
        <= 0x77 => "Budynki",
        _ => "Scenariusze"
    };

    private static string ScenarioWire(int scenario) => scenario switch
    {
        1 => "DR",
        2 => "DS",
        3 => "DT",
        4 => "DU",
        5 => "DW",
        6 => "DX",
        7 => "DY",
        _ => "DZ"
    };
}
