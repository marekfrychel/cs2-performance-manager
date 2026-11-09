# CS2 Performance Manager

Aplikacja WPF dla Windows 11 x64. Mapa rdzeni i wybór jednego wątku na rdzeń działają, gdy Windows udostępnia spójną topologię w jednej grupie procesorów.

## Uruchomienie

1. Zainstaluj [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0), jeśli nie jest zainstalowany.
2. Umieść `CS2PerformanceManager.exe` w docelowym folderze i uruchom go. Domyślny priorytet to **Normal**.
3. Wybierz **Windows** albo **Ręcznie**. W trybie ręcznym klikaj wątki na mapie lub użyj **Jeden wątek na rdzeń**. Gotowe profile grup i priorytet znajdziesz w rozwijanej sekcji. Wybierz **Normal**, **Above Normal** albo **High**. Kliknij **Zapisz i zastosuj**. Menedżer sprawdza co dwie sekundy, czy działa `cs2.exe`, i stosuje ustawienia również po ponownym uruchomieniu gry. Okno pokazuje wykrytą maskę oraz wynik operacji.
4. Opcjonalnie zaznacz autostart i zapisz ustawienia. Windows uruchomi aplikację w zasobniku po zalogowaniu na to konto. Jeśli przeniesiesz EXE, uruchom je z nowej lokalizacji i ponownie zapisz ustawienia.
5. Dwuklik ikony zasobnika otwiera okno. Krzyżyk i minimalizacja chowają okno. Polecenie **Zakończ** w menu ikony zatrzymuje monitorowanie. Parametry aktywnej gry pozostają do zamknięcia jej procesu lub ich kolejnej zmiany.

## Kompilacja

Zainstaluj [.NET 8 SDK x64](https://dotnet.microsoft.com/download/dotnet/8.0). W PowerShellu przejdź do folderu z plikiem projektu i wykonaj:

```powershell
dotnet build .\CS2PerformanceManager.csproj -c Release
dotnet publish .\CS2PerformanceManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o .\publish
```

Plik `publish\CS2PerformanceManager.exe` korzysta z zainstalowanego .NET 8 Desktop Runtime x64. Projekt nie ma zewnętrznych pakietów NuGet.

## Jak działa profil

- Aplikacja odczytuje maski rdzeni fizycznych i współdzielonej pamięci L3 z Windows. Mapę grupuje według rozłącznych domen L3, które obejmują wszystkie aktywne rdzenie. Udostępnia gotowe profile dla pierwszych dwóch grup. Na 5600X i 5900X wyświetla oznaczenia CCD, a na pozostałych modelach używa nazwy Grupa L3.
- W interfejsie **CCD1** oznacza domenę z wyższym spośród dwóch najniższych numerów procesorów logicznych w domenach. To kolejność na podstawie numeracji Windows. **− SMT** wybiera najniższy numer procesora logicznego na każdym rdzeniu domeny. **+ SMT** wybiera wszystkie jej wątki i jest dostępne, gdy Windows widzi dwa wątki na rdzeń.
- Jeśli nie można zweryfikować wybranego profilu lub mapy rdzeni, menedżer stosuje tylko priorytet. Każdy zapis parametrów procesu jest poprzedzony sprawdzeniem, czy plik wykonywalny na tym samym uchwycie to `cs2.exe`.
- **Ręcznie** pozwala zaznaczyć dostępne procesory logiczne na mapie rdzeni. Kliknięcie wątku z wybranym profilem przełącza na tryb ręczny. **Windows** zachowuje bieżącą maskę. Jeżeli menedżer ustawił maskę dla tego procesu podczas bieżącej sesji i od tego czasu nie zmieniła się ona, przywraca wcześniejszą maskę.
- **Zapisz i zastosuj** ponawia próbę z wybranym priorytetem i zestawem wątków. Błędy uprawnień i inne błędy Windows są widoczne w polu stanu.
- Ustawienia są zapisywane w `%LOCALAPPDATA%\CS2PerformanceManager\settings.json`. Autostart działa przez wpis `CS2PerformanceManager` w kluczu `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Stan pola autostartu przy otwarciu odzwierciedla wpis w rejestrze. Jeśli Windows odrzuci zmianę wpisu, okno wyświetli błąd osobno od wyniku zapisu JSON.

## Weryfikacja

Na Ryzen 9 5900X aplikacja wykryła CPU logiczne 12, 14, 16, 18, 20 i 22 (maska `0x555000`). Test odczytał z Windows poprawną maskę i trzy priorytety po ich ustawieniu na kontrolowanym procesie testowym `cs2.exe`. Potwierdził także, że proces o innej nazwie zachował swoją maskę i priorytet. Opublikowane EXE uruchomione z opcją --tray automatycznie wykryło nowy proces testowy i ustawiło tę samą maskę. Pełny test z grą i ponowne logowanie do Windows nie były wykonywane.

Dokumentacja [odczytu topologii procesorów w Windows](https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-getlogicalprocessorinformationex).




