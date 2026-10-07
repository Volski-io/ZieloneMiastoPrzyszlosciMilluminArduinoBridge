# Millumin UART Bridge

Bridge działający na macOS, który odbiera protokół makiety przez zwykły adapter USB–RS-232 i steruje Milluminem przez OSC. Program jest napisany w .NET 8. Arduino Uno pozostaje opcjonalnym adapterem zastępczym.

## Przepływ danych

```text
Makieta ⇄ UART/RS-232 ⇄ konwerter ⇄ USB Serial ⇄ Bridge ⇄ OSC ⇄ Millumin
```

Rdzeń protokołu nie zależy od `System.IO.Ports`, systemu plików ani implementacji UDP. Ułatwia to późniejsze przeniesienie parsera, CRC i logiki stanów do programu Crestron.

## Protokół makiety

Parametry portu:

- 2400 baud;
- 8 bitów danych;
- brak parzystości;
- 1 bit stopu;
- brak kontroli przepływu.

Każda ramka zawiera trzy bajty:

```text
0x29 | DATA | CRC8(DATA)
```

CRC-8 używa wielomianu `0x07`, wartości początkowej `0x00` i jest liczone wyłącznie z bajtu `DATA`.

| Scenariusz | DATA | ACK |
|---:|---:|---:|
| 1 | `0x78` | `0xF8` |
| 2 | `0x79` | `0xF9` |
| 3 | `0x7A` | `0xFA` |
| 4 | `0x7B` | `0xFB` |
| 5 | `0x7C` | `0xFC` |
| 6 | `0x7D` | `0xFD` |
| 7 | `0x7E` | `0xFE` |
| 8 | `0x7F` | `0xFF` |

Bridge odsyła ACK natychmiast po sprawdzeniu CRC. Wykonanie polecenia OSC nie blokuje odpowiedzi dla makiety. Uszkodzona suma powoduje wysłanie kodu `0x80`. Niespodziewany ACK powoduje wysłanie kodu `0x01`.

Przy 2400 baud przesłanie jednego bajtu w 8N1 trwa około 4,17 ms, a całej trzybajtowej ramki około 12,5 ms. Wspomniane w arkuszu 2 ms nie może więc być limitem pełnej wymiany po tym łączu. Bridge generuje odpowiedź bez zwłoki programowej, ale domyślnie oczekuje na pełny ACK przez 45 ms i zamyka okno ponowień po 100 ms. Obie wartości są konfigurowalne.

## Adapter USB–RS-232 — wariant podstawowy

Adapter podłącz bezpośrednio do portu USB komputera Mac oraz do interfejsu RS-232 makiety. Używane są tylko linie TX, RX i GND; kontrola przepływu jest wyłączona. Sprawdź układ pinów i potrzebę kabla prostego lub null-modem zgodnie z dokumentacją makiety i adaptera.

Bridge nie wymaga firmware ani specjalnej obsługi adaptera. Domyślne `StartupDelayMs` wynosi `0`, więc transmisja rusza od razu po otwarciu portu. Na macOS urządzenie powinno być widoczne najczęściej jako `/dev/cu.usbserial-*`, `/dev/cu.SLAB_USBtoUART*` albo `/dev/cu.wchusbserial*`.

## Arduino Uno jako adapter opcjonalny

Firmware dla Arduino Uno znajduje się w `firmware/ArduinoUnoUsbUartAdapter`. Korzysta z wbudowanego `SoftwareSerial` i nie wymaga dodatkowych bibliotek.

Połączenia po stronie TTL konwertera poziomów:

```text
TX makiety/konwertera → D10 Arduino Uno (RX)
RX makiety/konwertera ← D11 Arduino Uno (TX)
GND                   ↔ GND
```

Nie podłączaj napięć RS-232 bezpośrednio do pinów Uno. Pomiędzy makietą i pinami D10/D11 musi znaleźć się odpowiedni konwerter poziomów, np. układ klasy MAX3232.

Uno może resetować się podczas otwierania portu USB. Przy użyciu Uno ustaw `StartupDelayMs` na `2500`, aby bridge poczekał na zakończenie pracy bootloadera. W docelowej instalacji można dodatkowo wyłączyć auto-reset sprzętowo po wgraniu programu.

Alternatywny firmware dla płytki mającej sprzętowy `Serial1`, np. Mega lub Leonardo, znajduje się w `firmware/TransparentUsbUartBridge`.

## Konfiguracja Millumina

W panelu urządzeń Millumina (`⌘K`) otwórz OSC i ustaw:

- input port: `5000`;
- włącz `API feedback`;
- dodaj serwer feedback `127.0.0.1:5001`.

Bridge wysyła:

```text
/action/launchColumn [index albo "nazwa"]
/action/selectBoard ["nazwa"]
/ping
```

Odbiera między innymi:

```text
/millumin/board/launchedColumn [index, "nazwa"]
/millumin/board/stoppedColumn [index, "nazwa"]
```

## Konfiguracja bridge'a

Skopiuj `config/appsettings.example.json` jako `appsettings.json`. Dla stabilnego projektu zalecane jest mapowanie scen po nazwach:

```json
{
  "Scenario": 1,
  "Column": {
    "Index": null,
    "Name": "Scenariusz 1",
    "Board": "Makieta"
  }
}
```

Jeżeli `Serial.PortName` jest pusty, program szuka portu według `PortPatterns`. Gdy pasuje więcej niż jeden port, domyślnie zatrzymuje wybór zamiast łączyć się z przypadkowym urządzeniem. Można wtedy wpisać pełną ścieżkę, np. `/dev/cu.usbserial-0001`.

## Uruchomienie lokalne

```bash
cp config/appsettings.example.json src/Bridge.Host/appsettings.json
dotnet run --project src/Bridge.Host -- --config src/Bridge.Host/appsettings.json
```

Walidacja konfiguracji bez otwierania portów:

```bash
dotnet run --project src/Bridge.Host -- --config config/appsettings.example.json --validate-config
```

## Instalacja i automatyczny restart na macOS

```bash
chmod +x scripts/install-macos.sh
./scripts/install-macos.sh
```

Skrypt publikuje samodzielną aplikację dla Apple Silicon albo Intel, instaluje ją w `~/Library/Application Support/MilluminArduinoBridge` i rejestruje `LaunchAgent` z `RunAtLoad` oraz `KeepAlive`.

Konfiguracja po instalacji:

```text
~/Library/Application Support/MilluminArduinoBridge/appsettings.json
```

Logi:

```text
~/Library/Logs/MilluminArduinoBridge/
```

Ostatni żądany i zaobserwowany scenariusz jest zapisywany atomowo w `~/.local/share/MilluminArduinoBridge/state.json`. Po ponownym pojawieniu się feedbacku OSC bridge porównuje stan i, jeśli trzeba, jednokrotnie odtwarza ostatni żądany scenariusz.

Bridge uruchamia się automatycznie, ale nie uruchamia aplikacji Millumin. Aby cały komputer wracał do pracy po restarcie, dodaj Millumin do elementów logowania macOS i skonfiguruj w nim otwieranie właściwego projektu. Bridge zaczeka na pojawienie się OSC i wtedy przeprowadzi rekoncyliację stanu.

## Testy

```bash
dotnet run --project tests/Bridge.Tests
```

Testy nie wymagają sprzętu. Sprawdzają wektory CRC, parser strumieniowy, mapowanie scenariuszy i ACK, kodowanie OSC oraz podstawowe zachowanie odtwarzania stanu.
