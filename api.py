"""DeepSeek API 余额查询"""
import requests
from email.utils import parsedate_to_datetime
from html import unescape
import re
from xml.etree import ElementTree


class ApiError(Exception):
    """带用户友好提示的 API 错误"""


BALANCE_URL = "https://api.deepseek.com/user/balance"
STATUS_URL = "https://status.deepseek.com/api/v2/summary.json"
INCIDENTS_URL = "https://status.deepseek.com/api/v2/incidents.json"
HISTORY_RSS_URL = "https://status.deepseek.com/history.rss"
STATUS_PAGE_URL = "https://status.deepseek.com/"
TIMEOUT = 15
STATUS_HEADERS = {
    "Accept": "application/json, text/plain, */*",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                  "AppleWebKit/537.36 Chrome/131.0 Safari/537.36",
}


def fetch_balance(api_key: str) -> dict:
    """查询余额，返回 DeepSeek /user/balance 接口的原始 JSON。"""
    if not api_key or not api_key.strip():
        raise ApiError("还没有设置 API Key 哦，右键小窗选「设置 API Key」吧～")

    try:
        resp = requests.get(
            BALANCE_URL,
            headers={
                "Authorization": f"Bearer {api_key.strip()}",
                "Accept": "application/json",
            },
            timeout=TIMEOUT,
        )
    except requests.RequestException as exc:
        raise ApiError(f"网络好像走丢了（{exc.__class__.__name__}），请稍后再试") from exc

    if resp.status_code == 401:
        raise ApiError("API Key 好像不对哦，请检查后再试 (401)")
    if resp.status_code == 402:
        raise ApiError("账户余额不足啦，先去充值吧 (402)")
    if resp.status_code != 200:
        raise ApiError(f"服务器有点小情绪，状态码 {resp.status_code}")

    try:
        data = resp.json()
    except ValueError as exc:
        raise ApiError("返回的数据看不太懂，请稍后再试") from exc

    if not isinstance(data, dict) or not isinstance(data.get("balance_infos"), list):
        raise ApiError("返回的数据里没有余额信息")
    return data


def fetch_service_status() -> dict:
    """读取 DeepSeek 官方 Statuspage 的服务摘要。"""
    try:
        resp = requests.get(STATUS_URL, headers=STATUS_HEADERS, timeout=TIMEOUT)
        resp.raise_for_status()
        data = resp.json()
        if isinstance(data, dict) and isinstance(data.get("status"), dict):
            # summary 只含未解决事件；额外拉取历史事件以供界面展示。
            data["recent_incidents"] = _fetch_recent_incidents(
                data.get("incidents") or []
            )
            return data
    except (requests.RequestException, ValueError) as exc:
        primary_error = exc
    else:
        primary_error = ValueError("状态摘要格式不正确")

    # 部分网络会拦截 Statuspage 的 JSON 接口；退回官网首页仍能给出总体状态。
    try:
        page = requests.get(STATUS_PAGE_URL, headers=STATUS_HEADERS, timeout=TIMEOUT)
        page.raise_for_status()
        text = page.text.lower()
        operational = "all systems operational" in text or "所有系统正常" in page.text
        return {
            "status": {
                "indicator": "none" if operational else "unknown",
                "description": "All Systems Operational" if operational else "请前往官方状态页查看详情",
            },
            "components": (
                [
                    {"name": "API 服务", "status": "operational"},
                    {"name": "网页对话服务", "status": "operational"},
                ]
                if operational else []
            ),
            "incidents": [],
            "recent_incidents": _fetch_recent_incidents([]),
        }
    except requests.RequestException as fallback_error:
        raise ApiError(
            "无法连接 DeepSeek 官方状态页（%s / %s）"
            % (primary_error.__class__.__name__, fallback_error.__class__.__name__)
        ) from fallback_error


def _fetch_recent_incidents(default: list) -> list:
    """优先读取 JSON 历史事件；若被拦截则解析官方 RSS 历史订阅源。"""
    try:
        response = requests.get(INCIDENTS_URL, headers=STATUS_HEADERS, timeout=TIMEOUT)
        response.raise_for_status()
        payload = response.json()
        incidents = payload.get("incidents") or []
        if incidents:
            return incidents
    except (requests.RequestException, ValueError, AttributeError):
        pass

    try:
        response = requests.get(HISTORY_RSS_URL, headers=STATUS_HEADERS, timeout=TIMEOUT)
        response.raise_for_status()
        root = ElementTree.fromstring(response.content)
        incidents = []
        for item in root.findall("./channel/item"):
            title = _clean_feed_text(item.findtext("title") or "服务事件")
            detail = _clean_feed_text(item.findtext("description") or "状态已更新")
            published = item.findtext("pubDate") or ""
            try:
                published = parsedate_to_datetime(published).isoformat()
            except (TypeError, ValueError):
                pass
            incidents.append({
                "name": title,
                "status": "resolved",
                "updated_at": published,
                "incident_updates": [{"body": detail, "updated_at": published}],
            })
        return incidents or default
    except (requests.RequestException, ElementTree.ParseError):
        return default


def _clean_feed_text(text: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", unescape(text))).strip()
