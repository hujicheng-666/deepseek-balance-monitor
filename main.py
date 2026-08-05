"""DeepSeek 余额监视器 —— 程序入口

一个可爱的桌面悬浮小窗，实时显示 DeepSeek API 账户余额。
运行：python main.py
"""
import sys

from PySide6.QtWidgets import QApplication

from widget import BalanceWidget


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("DeepSeek 余额监视器")

    screen = app.primaryScreen().availableGeometry()
    widget = BalanceWidget()

    # 默认放到屏幕右上角，像个小挂件
    margin = 24
    x = screen.x() + screen.width() - widget.width() - margin
    y = screen.y() + margin
    widget.move(x, y)
    widget.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
