"""简单的 JSON 配置读写（存本地 config.json）"""
import json
import os

CONFIG_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(CONFIG_DIR, "config.json")

DEFAULTS = {
    "api_key": "",            # DeepSeek API Key
    "refresh_interval": 60,   # 自动刷新间隔（秒）
    "low_threshold": 10.0,    # 低余额提醒阈值
    "low_warn": True,         # 是否开启低余额提醒
}


def load_config() -> dict:
    cfg = dict(DEFAULTS)
    try:
        if os.path.exists(CONFIG_PATH):
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict):
                for k in DEFAULTS:
                    if k in loaded:
                        cfg[k] = loaded[k]
    except Exception:
        pass
    return cfg


def save_config(cfg: dict) -> None:
    try:
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception:
        pass
