"""
TuneBridge 2.0 — FastAPI entry point.
"""
from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from app.api.routes.transfer import router as transfer_router
from app.adapters.registry import list_providers

app = FastAPI(
    title="TuneBridge",
    description="One library. Every music platform.",
    version="2.0.0-dev",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(transfer_router)

static_dir = Path(__file__).parent.parent / "static"
if static_dir.exists():
    app.mount("/static", StaticFiles(directory=str(static_dir)), name="static")


@app.get("/api/health")
async def health():
    return {"status": "ok", "version": "2.0.0-dev"}


@app.get("/api/providers")
async def providers():
    result = []
    for p in list_providers():
        caps = p.get_capabilities()
        result.append({
            "id": p.provider_id,
            "name": p.display_name,
            "experimental": p.is_experimental,
            "capabilities": caps.model_dump(),
        })
    return result
