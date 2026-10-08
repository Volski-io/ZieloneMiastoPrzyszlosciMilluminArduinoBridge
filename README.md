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

### Test na Windows

W PowerShellu, w katalogu projektu:

```powershell
.\scripts\run-windows.ps1
```

Przy pierwszym uruchomieniu powstanie `config/appsettings.windows.json`. Program automatycznie wykrywa porty `COM*`; gdy jest ich kilka, wpisz konkretną nazwę w `Serial.PortName`, np. `COM4`.

Panel testowy jest dostępny lokalnie pod adresem:

```text
http://127.0.0.1:8080
```

Domyślna konfiguracja nasłuchuje na `http://0.0.0.0:8080`, więc panel jest też
dostępny z innych urządzeń w sieci lokalnej pod adresem IP komputera z bridgem,
np. `http://192.168.50.197:8080`. Na Windows uruchom jako Administrator:

```powershell
.\scripts\open-windows-firewall.ps1
```

Reguły zapory dopuszczają TCP `8080` i feedback OSC UDP `5001` wyłącznie z
lokalnej podsieci. Na macOS zezwól aplikacji `millumin-bridge` na połączenia
przychodzące w ustawieniach firewalla. Nie wystawiaj portu `8080` do Internetu.

Panel zawiera wszystkie wartości DATA `0x00–0xFF`, opis funkcji, gotową ramkę z CRC, oczekiwany ACK i odpowiadające wywołanie OSC. Rejestruje ramki UART i pakiety OSC w obu kierunkach, pokazując tłumaczenie oraz surowe bajty.

Każda wysyłalna komenda ma czytelny adres OSC. Wiadomość należy wysłać na port
feedback bridge'a, domyślnie UDP `5001`. Przykładowo:

```text
/makieta/sektory/4/przelacz
/makieta/scenariusz/1/uruchom
```

Adresy OSC używają wyłącznie znaków ASCII. Polskie znaki w nazwach urządzeń są
transliterowane, np. `Elektrownia Złotniki` ma adres
`/makieta/obiekty/elektrownia-zlotniki-on`.

Pierwszy adres wysyła DATA `0x54`, czyli ramkę `29 54 AB`. Drugi uruchamia
scenariusz 1 komendą DATA `0x78`. Pełna lista adresów znajduje się w panelu
testowym obok każdej komendy.

Bridge obsługuje także parametryzowane sekwencje:

```text
/makieta/balon/2/predkosc/70
/makieta/wiatraki/1-5/predkosc/70
/makieta/obiekty/chmura-pompka/predkosc/40
/makieta/transport/pociag/predkosc/40
/makieta/slupy/sektor/3/on
/makieta/slupy/sektory/2-6/on
```

Pierwsza sekwencja wysyła wartość `70%` (`0x16`), czeka na ACK `0x96`, a potem
uruchamia balon 2 (`0x1D`). Druga ustawia `70%` raz i uruchamia pięć wiatraków.
Trzecia ustawia `40%` (`0x13`) i uruchamia pompkę chmury (`0x4F`), a czwarta
z tą samą prędkością uruchamia pociąg (`0x4B`). Piąta czyści wybór sektorów
(`0x59`), wybiera sektor 3 (`0x53`) i stosuje wybór do słupów (`0x21`). Szósta
robi to samo dla całego domkniętego zakresu. Każdy krok czeka na własny ACK.

Numerowane wiatraki i balony obsługują komendy pojedyncze oraz zakresowe:

```text
/makieta/wiatrak/{1-5}/predkosc/{10-100}
/makieta/wiatraki/{od}-{do}/predkosc/{10-100}
/makieta/wiatraki/{od}-{do}/off
/makieta/balon/{1-3}/predkosc/{10-100}
/makieta/balony/{od}-{do}/predkosc/{10-100}
/makieta/balony/{od}-{do}/off
```

Prędkość musi być wielokrotnością `10%`. Zakres jest domknięty, rosnący i musi
zawierać co najmniej dwa elementy. Bridge ustawia aktywną wartość tylko raz, a
następnie uruchamia po kolei wszystkie urządzenia z zakresu.

Pompka chmury obsługuje analogiczną komendę
`/makieta/obiekty/chmura-pompka/predkosc/{10-100}`. Bridge ustawia aktywną
wartość, czeka na ACK i dopiero wtedy uruchamia pompkę.

Pociąg obsługuje komendę `/makieta/transport/pociag/predkosc/{10-100}` według
tej samej zasady: aktywna wartość, ACK, a następnie uruchomienie pociągu.

Ten sam mechanizm działa dla wszystkich urządzeń sektorowych:

| Element | Zakres | Ścieżka OSC |
| --- | ---: | --- |
| Słupy HV | 1–6 | `/makieta/slupy/sektor/{sektor}/on` |
| LED-y na balonach | 1–3 | `/makieta/balony/led/sektor/{sektor}/on` |
| Zabudowa mieszkaniowa 0 | 1–3 | `/makieta/zabudowa-mieszkaniowa/0/sektor/{sektor}/on` |
| Budynek 1 | 1–6 | `/makieta/budynek/1/sektor/{sektor}/on` |
| Budynek 2 | 1–7 | `/makieta/budynek/2/sektor/{sektor}/on` |
| Budynek 3 | 1–8 | `/makieta/budynek/3/sektor/{sektor}/on` |
| Hotel 1 | 1–3 | `/makieta/hotel/1/sektor/{sektor}/on` |
| Zabudowa mieszkaniowa 1 | 1–3 | `/makieta/zabudowa-mieszkaniowa/1/sektor/{sektor}/on` |
| Farma fotowoltaiczna 1 RGB | 1–3 | `/makieta/farma-fotowoltaiczna/1/rgb/sektor/{sektor}/on` |
| Magazyn energii RGB | 1–2 | `/makieta/magazyn-energii/rgb/sektor/{sektor}/on` |

Panel WWW udostępnia osobne sterowanie prędkością pompki chmury i pociągu,
wspólną listę urządzeń sektorowych i automatycznie ogranicza dostępne numery
sektorów do zakresu wybranego elementu.

Każdy element sektorowy z tabeli obsługuje również zakres:

```text
/makieta/{element}/sektory/{od}-{do}/on
```

Przykładowo `/makieta/budynek/3/sektory/2-7/on` czyści wybór, zaznacza sektory
2–7 i stosuje go do budynku 3. Zakres jest domknięty i musi mieścić się w
zakresie elementu podanym w tabeli.

Wybór sektorów jest globalny dla elementów opisanych zakresami w nawiasach:

- `0x50` wybiera wszystkie sektory;
- `0x51–0x58` przełącza wybór danego sektora;
- `0x59` czyści cały wybór;
- komenda ON urządzenia sektorowego stosuje aktualny wybór. Jeżeli wybór jest
  pusty, np. po `0x59`, komenda ON taka jak `0x23` wyłącza wszystkie elementy.

Dla zgodności ze starszymi integracjami nadal działają adresy bajtowe:

```text
/bridge/model/command/54
/bridge/model/command 84
```

### Samodzielny tester OSC na Windows

Uruchom dwuklikiem `scripts\osc-tester.cmd` albo z PowerShella:

```powershell
.\scripts\osc-tester.ps1
```

Okno pozwala podać adres docelowy, port UDP, adres OSC i argumenty w formacie
JSON. Gotowe presety testują sekwencję balonu 2 z prędkością 70%, uruchomienie
sektora 3 słupów oraz `/ping` do Millumina.

Wysyłka bez otwierania okna:

```powershell
.\scripts\osc-tester.ps1 -SendOnce -TargetHost 127.0.0.1 -Port 5001 `
  -Address /makieta/balon/2/predkosc/70 -ArgumentsJson "[]"
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
