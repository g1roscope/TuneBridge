# Окончательная архитектура TuneBridge

## Решение

- **Desktop**: C# / .NET 8 + WPF (self-contained)
- **API**: Python FastAPI + HTTPX
- **Future Web**: React + TypeScript + Tailwind на том же API

Пользователь Windows не ставит Python.

## Структура

```
src/
  TuneBridge.Api/       FastAPI + adapters + matching
  TuneBridge.Desktop/   C# WPF
  TuneBridge.Core/      shared contracts
  TuneBridge.Web/       future React
docs/
```

## Принципы

- Guest First
- Official OAuth only (кроме Experimental)
- SSRF whitelist
- Токены server-side
- Никаких фиктивных успехов
