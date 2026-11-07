# Аудит TuneBridge 0.1.0

Дата аудита: 2026-11-06

## Структура

```
tunebridge/
├── pyproject.toml, requirements.txt, LICENSE, README.md
├── tests/test_matcher.py
└── tunebridge/
    ├── models.py
    ├── matcher.py
    ├── transfer.py
    ├── app.py
    ├── adapters/base.py, local.py
    └── static/
```

## Что реально работает

- Импорт JSON/CSV
- Matching (ISRC + title/artist/album/duration)
- Статусы matched / ambiguous / missing
- Экспорт JSON/CSV
- Простой HTML UI
- 4 unit-теста matcher

## Что отсутствует

- Живые музыкальные сервисы
- OAuth
- parse_playlist_url, get_capabilities
- Создание плейлистов
- Windows Desktop

## Вывод

0.1.0 — качественное ядро matching + file transfer. Расширять, не выбрасывать.
