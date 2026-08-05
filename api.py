"""DeepSeek API 余额查询"""
import requests


class ApiError(Exception):
    """带用户友好提示的 API 错误"""


BALANCE_URL = "https://api.deepseek.com/user/balance"
TIMEOUT = 15


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
