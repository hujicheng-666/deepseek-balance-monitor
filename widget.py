"""DeepSeek —— 桌面悬浮窗"""
import threading
import time
from datetime import datetime

from PySide6.QtCore import (
    QAbstractAnimation,
    QEasingCurve,
    QPoint,
    QPropertyAnimation,
    QRect,
    QSize,
    Qt,
    QTimer,
    Signal,
)
from PySide6.QtGui import (
    QBrush,
    QColor,
    QCursor,
    QFont,
    QGuiApplication,
    QLinearGradient,
    QPainter,
    QPen,
    QRadialGradient,
)
from PySide6.QtWidgets import (
    QApplication,
    QHBoxLayout,
    QGraphicsEffect,
    QInputDialog,
    QLabel,
    QLineEdit,
    QMenu,
    QMessageBox,
    QScrollArea,
    QSizePolicy,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

import api
import config as cfg

# ---------------- 配色（HarmonyOS 沉浸光感：石墨玻璃 + 银白反射） ----------------
COLOR_BG_TOP = QColor(40, 42, 48, 210)
COLOR_BG_BOTTOM = QColor(15, 16, 20, 226)
COLOR_BORDER = QColor("#777B86")
COLOR_BORDER_HOVER = QColor("#D7DBE4")
COLOR_TEXT = "#F4F5F7"
COLOR_SUB = "#B7BAC2"
COLOR_ACCENT = "#FFFFFF"
COLOR_GREEN = "#56C596"
COLOR_AMBER = "#E5B568"
COLOR_CORAL = "#ED7C72"

HIDE_DELAY = 700     # 鼠标移开后收回的延迟（毫秒）
DOCK_THRESHOLD = 36  # 拖到离屏幕边缘多近就自动贴边
CARD_SIZE = QSize(310, 174)
ORB_SIZE = 32        # 收起后「鲸鱼精灵球」的直径


class TimelineWheelEffect(QGraphicsEffect):
    """把远离中心的时间线行压扁，形成滚轮的圆柱透视。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self._position = 0.0

    def set_position(self, position):
        self._position = max(-1.0, min(1.0, position))
        self.update()

    def draw(self, painter):
        # PySide6 通过可变 QPoint 参数返回源图偏移，而非返回元组。
        offset = QPoint()
        pixmap = self.sourcePixmap(Qt.LogicalCoordinates, offset)
        amount = abs(self._position)
        painter.save()
        painter.setOpacity(1.0 - amount * 0.76)
        center_x = offset.x() + pixmap.width() / 2
        center_y = offset.y() + pixmap.height() / 2
        painter.translate(center_x, center_y)
        # 越靠近上下边缘，纵向越扁，模拟圆柱表面转离视线。
        painter.scale(1.0, 1.0 - amount * 0.48)
        painter.translate(-center_x, -center_y)
        painter.drawPixmap(offset, pixmap)
        painter.restore()


class TimelineCanvas(QWidget):
    """在同一画布上绘制节点与连线，保证相邻事件始终相连。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.rows = []
        self.row_opacities = []

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        x = 7
        centers = [row.y() + 8 for row in self.rows]
        for index, center_y in enumerate(centers):
            alpha = int(230 * (self.row_opacities[index] if index < len(self.row_opacities) else 1))
            if index < len(centers) - 1:
                next_alpha = int(230 * (
                    self.row_opacities[index + 1]
                    if index + 1 < len(self.row_opacities) else 1
                ))
                painter.setPen(QPen(QColor(190, 195, 205, min(alpha, next_alpha)), 1))
                painter.drawLine(x, center_y + 4, x, centers[index + 1])
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(225, 228, 234, alpha))
            painter.drawEllipse(QPoint(x, center_y), 3, 3)


class BalanceWidget(QWidget):
    """无边框、透明、置顶的悬浮余额卡片"""

    data_ready = Signal(tuple)  # (state, data, error)
    service_status_ready = Signal(tuple)  # (data, error)

    def __init__(self):
        super().__init__()
        self._drag_offset = None
        self._press_pos = None
        self._dragging = False
        self._hovered = False
        self._menu_open = False
        self._service_view = False
        self._timeline_rows = []
        self._dock_edge = None   # None / 'left' / 'right'
        self._docked = False     # 是否贴边
        self._peeking = False    # 贴边时是否探出
        self._cfg = cfg.load_config()
        self._dismissing = False
        self._dismiss_started = 0.0
        self._dismiss_particles = []
        self._dismiss_snapshot = None

        self.setWindowFlags(
            Qt.FramelessWindowHint
            | Qt.WindowStaysOnTopHint
            | Qt.Tool                       # 不占任务栏，更像小挂件
        )
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setMinimumSize(ORB_SIZE, ORB_SIZE)
        self.setMaximumSize(CARD_SIZE)
        self.resize(CARD_SIZE)
        self.setToolTip("右键可以设置 / 刷新 / 退出")

        self._build_ui()
        self._build_menu()

        self._wheel_snap_timer = QTimer(self)
        self._wheel_snap_timer.setSingleShot(True)
        self._wheel_snap_timer.timeout.connect(self._snap_timeline_to_center)
        self.service_timeline.verticalScrollBar().valueChanged.connect(
            self._update_timeline_wheel
        )

        self.data_ready.connect(self._on_data)
        self.service_status_ready.connect(self._on_service_status)

        # 自动刷新
        self._timer = QTimer(self)
        self._timer.timeout.connect(self.refresh)
        self._interval = max(30, int(self._cfg.get("refresh_interval", 60)))
        self._timer.start(self._interval * 1000)

        # 侧边收起 / 探出
        self._hide_timer = QTimer(self)
        self._hide_timer.setSingleShot(True)
        self._hide_timer.timeout.connect(self._schedule_hide)

        self._anim = QPropertyAnimation(self, b"geometry", self)
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.InOutCubic)
        self._anim.finished.connect(self._finish_dock_animation)
        self._animation_kind = None

        self._dismiss_timer = QTimer(self)
        self._dismiss_timer.timeout.connect(self._tick_dismiss)

        # 初始状态
        if self._cfg.get("api_key"):
            self.set_status("loading", "准备中")
            QTimer.singleShot(150, self.refresh)
        else:
            self.set_status("no_key", "未设置")
            QTimer.singleShot(350, self.first_run_guide)

    # ---------------- UI ----------------
    def _build_ui(self):
        root = QVBoxLayout(self)
        root.setContentsMargins(16, 12, 16, 10)
        root.setSpacing(0)

        # 头部：鲸鱼 + 标题 + 状态 + 刷新按钮
        header = QHBoxLayout()
        header.setSpacing(8)

        self.emoji = QLabel("🐳")
        self.emoji.setFont(QFont("Segoe UI Emoji", 17))
        self.emoji.setStyleSheet("background:transparent;")

        self.title_label = QLabel("DeepSeek")
        self.title_label.setStyleSheet(
            f"font-family:'Microsoft YaHei UI';font-size:13px;"
            f"font-weight:600;color:{COLOR_TEXT};background:transparent;"
        )

        self.status_dot = QLabel("●")
        self.status_dot.setStyleSheet(
            f"color:{COLOR_AMBER};font-size:12px;background:transparent;"
        )
        self.status_label = QLabel("准备中")
        self.status_label.setStyleSheet(
            f"font-family:'Microsoft YaHei UI';font-size:10px;"
            f"color:{COLOR_SUB};background:transparent;"
        )

        self.btn_min = QToolButton(self)
        self.btn_min.setText("–")
        self.btn_min.setCursor(Qt.PointingHandCursor)
        self.btn_min.setToolTip("收进屏幕侧边")
        self.btn_min.setStyleSheet(
            "QToolButton{background:transparent;color:#9A9EA8;border:none;"
            "font-family:'Segoe UI';font-size:16px;padding:2px 4px;}"
            "QToolButton:hover{color:" + COLOR_ACCENT + ";}"
        )
        self.btn_min.clicked.connect(self.hide_to_side)

        self.btn_service = QToolButton(self)
        self.btn_service.setText("◉")
        self.btn_service.setCursor(Qt.PointingHandCursor)
        self.btn_service.setToolTip("查看 DeepSeek 服务状态")
        self.btn_service.setStyleSheet(
            "QToolButton{background:transparent;color:#9A9EA8;border:none;"
            "font-family:'Segoe UI';font-size:14px;padding:2px 3px;}"
            "QToolButton:hover{color:" + COLOR_ACCENT + ";}"
        )
        self.btn_service.clicked.connect(self.toggle_service_view)

        self.btn_refresh = QToolButton(self)
        self.btn_refresh.setText("↻")
        self.btn_refresh.setCursor(Qt.PointingHandCursor)
        self.btn_refresh.setToolTip("立即刷新")
        self.btn_refresh.setStyleSheet(
            "QToolButton{background:transparent;color:#9A9EA8;border:none;"
            "font-family:'Segoe UI';font-size:17px;padding:2px 4px;}"
            "QToolButton:hover{color:" + COLOR_ACCENT + ";}"
        )
        self.btn_refresh.clicked.connect(self.refresh)

        header.addWidget(self.emoji)
        header.addWidget(self.title_label)
        header.addStretch(1)
        header.addWidget(self.status_dot)
        header.addWidget(self.status_label)
        header.addWidget(self.btn_service)
        header.addWidget(self.btn_min)
        header.addWidget(self.btn_refresh)
        root.addLayout(header)

        # 余额大数字
        self.balance = QLabel("--.--")
        self.balance.setAlignment(Qt.AlignHCenter | Qt.AlignVCenter)
        self._set_balance_style(COLOR_ACCENT)
        root.addSpacing(0)
        root.addWidget(self.balance)

        self.balance_note = QLabel("正在查看小金库…")
        self.balance_note.setAlignment(Qt.AlignHCenter)
        self.balance_note.setStyleSheet(
            f"font-family:'Microsoft YaHei UI';font-size:11px;"
            f"color:{COLOR_SUB};background:transparent;"
        )
        root.addWidget(self.balance_note)

        # 服务状态页：与余额内容共用同一张卡片，不增加窗口尺寸。
        self.service_panel = QWidget(self)
        self.service_panel.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self.service_panel.setAttribute(Qt.WA_TranslucentBackground)
        self.service_panel.setStyleSheet("background:transparent;")
        service_layout = QVBoxLayout(self.service_panel)
        service_layout.setContentsMargins(4, 5, 4, 0)
        service_layout.setSpacing(5)
        self.service_timeline = QScrollArea(self.service_panel)
        self.service_timeline.setWidgetResizable(True)
        self.service_timeline.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.service_timeline.setAutoFillBackground(False)
        self.service_timeline.viewport().setAutoFillBackground(False)
        self.service_timeline.viewport().setAttribute(Qt.WA_TranslucentBackground)
        self.service_timeline.setStyleSheet(
            "QScrollArea{border:0;background:transparent;}"
            "QScrollArea > QWidget > QWidget{background:transparent;border:0;}"
            "QScrollBar:vertical{width:4px;background:transparent;margin:2px;}"
            "QScrollBar::handle:vertical{background:#6C707A;border-radius:2px;min-height:18px;}"
        )
        self.timeline_content = TimelineCanvas(self.service_timeline)
        self.timeline_content.setAttribute(Qt.WA_TranslucentBackground)
        self.timeline_content.setStyleSheet("background:transparent;")
        self.timeline_layout = QVBoxLayout(self.timeline_content)
        self.timeline_layout.setContentsMargins(0, 0, 3, 0)
        self.timeline_layout.setSpacing(0)
        self.service_timeline.setWidget(self.timeline_content)
        service_layout.addWidget(self.service_timeline, 1)
        self.service_panel.hide()
        root.addWidget(self.service_panel, 1)

        # 底部信息
        self.bottom_row = QWidget(self)
        bottom = QHBoxLayout(self.bottom_row)
        bottom.setContentsMargins(0, 0, 0, 0)
        bottom.setSpacing(6)
        self.lbl_topped = self._info_label("充值 --")
        self.lbl_granted = self._info_label("赠送 --")
        self.lbl_time = self._info_label("更新 --:--")
        bottom.addWidget(self.lbl_topped)
        bottom.addStretch(1)
        bottom.addWidget(self.lbl_granted)
        bottom.addStretch(1)
        bottom.addWidget(self.lbl_time)
        # 余额页用它把底部信息压到卡片底部；服务页隐藏后，时间线可占满高度。
        self.balance_spacer = QWidget(self)
        self.balance_spacer.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        root.addWidget(self.balance_spacer, 1)
        root.addWidget(self.bottom_row)

    def _info_label(self, text):
        lbl = QLabel(text)
        lbl.setStyleSheet(
            f"font-family:'Microsoft YaHei UI';font-size:10px;"
            f"color:{COLOR_SUB};background:transparent;"
        )
        return lbl

    def _set_balance_style(self, color_hex):
        self.balance.setStyleSheet(
            f"font-family:'Segoe UI';font-size:34px;font-weight:700;"
            f"color:{color_hex};background:transparent;"
        )

    # ---------------- 右键菜单 ----------------
    def _build_menu(self):
        self._menu = QMenu(self)
        self._menu.setStyleSheet(
            "QMenu{background-color:#25262B;border:1px solid #5B5E67;"
            "border-radius:10px;padding:6px;}"
            "QMenu::item{padding:6px 24px 6px 12px;border-radius:6px;"
            "font-family:'Microsoft YaHei UI';font-size:12px;color:#F4F5F7;}"
            "QMenu::item:selected{background-color:#3A3C43;color:#FFFFFF;}"
            "QMenu::separator{height:1px;background:#454850;margin:4px 8px;}"
        )

        act_refresh = self._menu.addAction("立即刷新")
        act_refresh.triggered.connect(self.refresh)

        act_key = self._menu.addAction("设置 API Key")
        act_key.triggered.connect(self.ask_api_key)

        act_dock = self._menu.addAction("收进屏幕侧边")
        act_dock.triggered.connect(self.hide_to_side)

        # 刷新间隔子菜单
        self._interval_menu = self._menu.addMenu("自动刷新间隔")
        self._interval_menu.setStyleSheet(self._menu.styleSheet())
        for label, secs in [("30 秒", 30), ("1 分钟", 60), ("5 分钟", 300), ("15 分钟", 900)]:
            act = self._interval_menu.addAction(label)
            act.triggered.connect(lambda _=False, s=secs: self.set_interval(s))

        self._menu.addSeparator()
        self.act_low_warn = self._menu.addAction("余额偏低时提醒")
        self.act_low_warn.setCheckable(True)
        self.act_low_warn.setChecked(bool(self._cfg.get("low_warn", True)))
        self.act_low_warn.toggled.connect(self._toggle_low_warn)

        self._menu.addSeparator()
        act_quit = self._menu.addAction("退出")
        act_quit.triggered.connect(self.dismiss)

    def contextMenuEvent(self, event):
        # 菜单会让鼠标暂时离开卡片；显示期间不能触发贴边收回。
        self._cancel_hide()
        self._menu_open = True
        try:
            self._menu.exec(event.globalPos())
        finally:
            self._menu_open = False
        if self._docked and not self.geometry().contains(QCursor.pos()):
            self._hide_timer.start(HIDE_DELAY)

    # ---------------- 拖动 ----------------
    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._press_pos = event.globalPosition().toPoint()
            self._drag_offset = self._press_pos - self.frameGeometry().topLeft()
            self._dragging = False
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self._drag_offset is not None and event.buttons() & Qt.LeftButton:
            if not self._dragging:
                moved = (
                    event.globalPosition().toPoint() - self._press_pos
                ).manhattanLength()
                if moved > 4:
                    self._dragging = True
                    self._cancel_hide()
                    self._undock()  # 拖出侧边，恢复自由
            if self._dragging:
                self.move(event.globalPosition().toPoint() - self._drag_offset)
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton and self._drag_offset is not None:
            self._drag_offset = None
            if self._dragging:
                self._dragging = False
                self._maybe_dock_on_release()
        super().mouseReleaseEvent(event)

    def enterEvent(self, event):
        self._hovered = True
        self.update()
        if self._docked:
            self._cancel_hide()
            if not self._peeking:
                self._peeking = True
                self._animate_to(self._full_geometry(self._dock_edge))
        super().enterEvent(event)

    def leaveEvent(self, event):
        self._hovered = False
        self.update()
        if self._docked:
            self._hide_timer.start(HIDE_DELAY)
        super().leaveEvent(event)

    # ---------------- 绘制：圆角 + 渐变 ----------------
    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)

        if self._dismissing:
            self._draw_dismiss_particles(painter)
            return

        if self._docked and not self._peeking:
            self._draw_whale_orb(painter)
            return

        rect = self.rect().adjusted(1, 1, -1, -1)

        grad = QLinearGradient(0, 0, 0, rect.height())
        grad.setColorAt(0.0, COLOR_BG_TOP)
        grad.setColorAt(1.0, COLOR_BG_BOTTOM)

        painter.setBrush(QBrush(grad))
        painter.setPen(Qt.NoPen)
        painter.drawRoundedRect(rect, 26, 26)

        # 光线在玻璃材质内传播：上方入光、下方反射、边缘高亮。
        top_glow = QRadialGradient(rect.width() * 0.16, rect.height() * 0.05, rect.width() * 0.72)
        top_glow.setColorAt(0, QColor(255, 255, 255, 84 if self._hovered else 58))
        top_glow.setColorAt(0.55, QColor(220, 224, 235, 22))
        top_glow.setColorAt(1, QColor(30, 31, 36, 0))
        painter.setBrush(top_glow)
        painter.drawRoundedRect(rect, 26, 26)

        bottom_reflection = QRadialGradient(rect.width() * 0.82, rect.height() * 1.05, rect.width() * 0.58)
        bottom_reflection.setColorAt(0, QColor(232, 202, 150, 26))
        bottom_reflection.setColorAt(1, QColor(60, 56, 48, 0))
        painter.setBrush(bottom_reflection)
        painter.drawRoundedRect(rect, 26, 26)

        border = COLOR_BORDER_HOVER if self._hovered else COLOR_BORDER
        painter.setBrush(Qt.NoBrush)
        painter.setPen(QPen(border, 1.2))
        painter.drawRoundedRect(rect, 26, 26)

    def dismiss(self):
        """将当前卡片采样为碎片，按各自轨迹消散后退出。"""
        if self._dismissing:
            return
        # 先捕获完整卡片；后续每个粒子都从这张画面中取一个小碎片。
        self._dismiss_snapshot = self.grab()
        self._dismissing = True
        self._dismiss_started = time.monotonic()
        self._set_card_content_visible(False)
        # 以 1px 网格切开整个表面：310×174 的卡片约有 54,000 个微粒。
        # 所有颗粒从自己的原始像素位置起飞，形成风化而非爆炸的观感。
        grain = 1
        self._dismiss_particles = []
        for sy in range(0, self.height(), grain):
            for sx in range(0, self.width(), grain):
                noise = ((sx * 17 + sy * 31) % 101) / 100
                # 主风场向右，叠加少量上下乱流和重力下坠。
                vx = 54 + noise * 74
                vy = (noise - 0.5) * 42 + 18
                # 左侧先风化，右侧稍后被卷走，避免整块同时消失。
                delay = 0.03 + sx / max(1, self.width()) * 0.22 + noise * 0.10
                size = min(grain, self.width() - sx, self.height() - sy)
                self._dismiss_particles.append((sx, sy, vx, vy, size, delay))
        self._dismiss_timer.start(0)
        self.update()

    def _tick_dismiss(self):
        if time.monotonic() - self._dismiss_started >= 1.32:
            self._dismiss_timer.stop()
            self.hide()
            QApplication.quit()
            return
        self.update()

    def _draw_dismiss_particles(self, painter):
        elapsed = time.monotonic() - self._dismiss_started
        # 原卡片先逐步失去整体感，随后只保留被风带走的表面微粒。
        if self._dismiss_snapshot is not None:
            painter.setOpacity(max(0.0, 1 - elapsed / 0.34))
            painter.drawPixmap(self.rect(), self._dismiss_snapshot)

        for sx, sy, vx, vy, size, delay in self._dismiss_particles:
            local = min(1.0, max(0.0, (elapsed - delay) / 0.94))
            if local <= 0:
                continue
            ease = 1 - (1 - local) * (1 - local)
            px = sx + vx * ease
            py = sy + vy * ease + 22 * local * local
            alpha = (1 - local) ** 1.25
            source = QRect(sx, sy, size, size)
            target = QRect(int(px), int(py), size, size)
            painter.setOpacity(alpha)
            painter.drawPixmap(target, self._dismiss_snapshot, source)
        painter.setOpacity(1.0)


    # ---------------- 贴边收纳 / 探出 ----------------
    def _screen_geo(self):
        scr = self.screen() or QGuiApplication.primaryScreen()
        return scr.availableGeometry()

    def hide_to_side(self):
        """收进最近的左右侧边，缩成半露出的鲸鱼精灵球。"""
        edge, _ = self._nearest_edge()
        self._dock_edge = edge
        self._docked = True
        self._peeking = False
        self._cancel_hide()
        self._set_card_content_visible(False)
        self._animate_to(self._hidden_geometry(edge))

    def _nearest_edge(self, margin=DOCK_THRESHOLD):
        geo = self._screen_geo()
        r = self.geometry()
        cand = {
            "left": r.left() - geo.left(),
            "right": geo.right() - r.right(),
        }
        edge = min(cand, key=cand.get)
        return edge, cand[edge]

    def _maybe_dock_on_release(self):
        edge, dist = self._nearest_edge()
        if dist <= DOCK_THRESHOLD:
            self._dock_edge = edge
            self._docked = True
            self._peeking = False
            self._set_card_content_visible(False)
            self._animate_to(self._hidden_geometry(edge))

    def _undock(self):
        if self._docked and self._dock_edge:
            if self._anim.state() == QAbstractAnimation.Running:
                self._anim.stop()
            self.setGeometry(self._full_geometry(self._dock_edge))
            self._set_card_content_visible(True)
            self._apply_view_visibility()
        self._dock_edge = None
        self._docked = False
        self._peeking = False

    def _hidden_geometry(self, edge):
        """让圆球的一半留在屏幕内，方便鼠标唤醒。"""
        geo = self._screen_geo()
        center_y = self.geometry().center().y()
        y = max(geo.top(), min(center_y - ORB_SIZE // 2, geo.bottom() - ORB_SIZE + 1))
        if edge == "left":
            x = geo.left() - ORB_SIZE // 2
        else:
            x = geo.right() - ORB_SIZE // 2 + 1
        return QRect(x, y, ORB_SIZE, ORB_SIZE)

    def _full_geometry(self, edge):
        """探出时保持圆球原本的垂直中心，完整显示卡片。"""
        geo = self._screen_geo()
        center_y = self.geometry().center().y()
        y = max(geo.top(), min(center_y - CARD_SIZE.height() // 2, geo.bottom() - CARD_SIZE.height() + 1))
        if edge == "left":
            x = geo.left()
        else:
            x = geo.right() - CARD_SIZE.width() + 1
        return QRect(x, y, CARD_SIZE.width(), CARD_SIZE.height())

    def _animate_to(self, geometry):
        if self._anim.state() == QAbstractAnimation.Running:
            self._anim.stop()
        self._animation_kind = "expand" if self._peeking else "collapse"
        self._anim.setStartValue(self.geometry())
        self._anim.setEndValue(geometry)
        self._anim.start()

    def _finish_dock_animation(self):
        if self._animation_kind == "expand" and self._docked and self._peeking:
            self._set_card_content_visible(True)
            self._apply_view_visibility()
        self._animation_kind = None

    def _set_card_content_visible(self, visible):
        for child in self.findChildren(QLabel) + self.findChildren(QToolButton):
            child.setVisible(visible)

    def _apply_view_visibility(self):
        """在贴边展开后按当前页面模式恢复内容，避免两页叠加。"""
        show_service = self._service_view
        self.balance.setVisible(not show_service)
        self.balance_note.setVisible(not show_service)
        self.bottom_row.setVisible(not show_service)
        self.balance_spacer.setVisible(not show_service)
        self.service_panel.setVisible(show_service)

    def _cancel_hide(self):
        self._hide_timer.stop()

    def _schedule_hide(self):
        if not self._docked or not self._peeking or self._menu_open:
            return
        # 鼠标其实还在窗口里就不收回
        if self.geometry().contains(QCursor.pos()):
            return
        self._peeking = False
        self._set_card_content_visible(False)
        self._animate_to(self._hidden_geometry(self._dock_edge))

    def _draw_whale_orb(self, painter):
        """绘制收起态：带海浪光泽的鲸鱼精灵球。"""
        painter.save()
        painter.setRenderHint(QPainter.Antialiasing)
        circle = self.rect().adjusted(3, 3, -3, -3)
        gradient = QLinearGradient(circle.topLeft(), circle.bottomRight())
        gradient.setColorAt(0, QColor("#676A73"))
        gradient.setColorAt(0.52, QColor("#303238"))
        gradient.setColorAt(1, QColor("#17181C"))
        painter.setBrush(gradient)
        painter.setPen(QPen(QColor("#D7DBE4"), 1.4))
        painter.drawEllipse(circle)
        painter.setBrush(QColor(255, 255, 255, 38))
        painter.setPen(Qt.NoPen)
        painter.drawEllipse(circle.adjusted(3, 3, -3, -3))
        painter.setBrush(QColor(255, 255, 255, 54))
        painter.setPen(Qt.NoPen)
        painter.drawEllipse(circle.adjusted(5, 4, -10, -13))
        painter.setFont(QFont("Segoe UI Emoji", 16))
        painter.setPen(QColor("#FFFFFF"))
        painter.drawText(circle, Qt.AlignCenter, "🐳")
        painter.restore()

    # ---------------- 状态与数据 ----------------
    def set_status(self, state, text=None):
        dot = {
            "loading": COLOR_AMBER,
            "ok": COLOR_GREEN,
            "error": COLOR_CORAL,
            "no_key": COLOR_AMBER,
        }.get(state, COLOR_GREEN)
        label = {
            "loading": "加载中",
            "ok": "在线",
            "error": "出错了",
            "no_key": "未设置",
        }.get(state, "在线")
        self.status_dot.setStyleSheet(
            f"color:{dot};font-size:12px;background:transparent;"
        )
        self.status_label.setText(text or label)

    def refresh(self):
        key = (self._cfg.get("api_key") or "").strip()
        if not key:
            self.set_status("no_key")
            self.balance.setText("--.--")
            self.balance_note.setText("还没设置 API Key 哦")
            return

        self.set_status("loading", "看看中")
        self.balance.setText("···")

        def work():
            try:
                data = api.fetch_balance(key)
                self.data_ready.emit(("ok", data, None))
            except api.ApiError as exc:
                self.data_ready.emit(("error", None, str(exc)))
            except Exception as exc:  # 兜底
                self.data_ready.emit(("error", None, f"发生了一点意外：{exc}"))

        threading.Thread(target=work, daemon=True).start()

    def toggle_service_view(self):
        """在余额页和服务状态页之间切换，不改变悬浮窗尺寸。"""
        self._service_view = not self._service_view
        show_service = self._service_view
        self._apply_view_visibility()
        self.title_label.setText("DeepSeek 服务" if show_service else "DeepSeek")
        self.btn_service.setText("‹" if show_service else "◉")
        self.btn_service.setToolTip("返回余额" if show_service else "查看 DeepSeek 服务状态")
        if show_service:
            self.check_service_status()
        else:
            # 服务查询会临时覆盖状态文案；返回余额页时立即恢复余额连接状态。
            self.set_status("ok" if (self._cfg.get("api_key") or "").strip() else "no_key")

    def check_service_status(self):
        """异步查询官方服务状态，不阻塞悬浮窗操作。"""
        self.set_status("loading", "检查服务中")
        self._set_timeline_message("正在加载近期服务事件…")

        def work():
            try:
                self.service_status_ready.emit((api.fetch_service_status(), None))
            except api.ApiError as exc:
                self.service_status_ready.emit((None, str(exc)))
            except Exception as exc:
                self.service_status_ready.emit((None, f"发生了一点意外：{exc}"))

        threading.Thread(target=work, daemon=True).start()

    def _on_service_status(self, payload):
        data, error = payload
        if error:
            self.set_status("error", "服务状态未知")
            self._set_timeline_message("服务事件暂时无法读取\n" + error)
            return

        summary = data.get("status") or {}
        indicator = str(summary.get("indicator") or "unknown")
        description = str(summary.get("description") or "未知")
        indicator_text = {
            "none": "正常",
            "minor": "轻微故障",
            "major": "重大故障",
            "critical": "严重故障",
            "maintenance": "维护中",
        }.get(indicator, description)
        incidents = data.get("recent_incidents") or data.get("incidents") or []
        timeline_events = []
        for incident in incidents:
            if not isinstance(incident, dict):
                continue
            name = str(incident.get("name") or "服务事件")
            updates = incident.get("incident_updates") or []
            if not updates:
                updates = [{
                    "body": str(incident.get("status") or "状态已更新"),
                    "updated_at": incident.get("updated_at") or incident.get("created_at") or "",
                }]
            for update in updates:
                if not isinstance(update, dict):
                    continue
                timeline_events.append({
                    "time": str(update.get("updated_at") or ""),
                    "title": name,
                    "detail": str(update.get("body") or incident.get("status") or "状态已更新"),
                })
        timeline_events.sort(key=lambda item: item["time"], reverse=True)

        self.set_status("ok", "服务 " + indicator_text)
        self._render_timeline(timeline_events[:20])

    def _clear_timeline(self):
        self._timeline_rows = []
        self.timeline_content.rows = []
        self.timeline_content.row_opacities = []
        self._wheel_top_spacer = None
        self._wheel_bottom_spacer = None
        while self.timeline_layout.count():
            item = self.timeline_layout.takeAt(0)
            if item.widget():
                item.widget().deleteLater()

    def _set_timeline_message(self, text):
        self._clear_timeline()
        label = QLabel(text, self.timeline_content)
        label.setWordWrap(True)
        label.setStyleSheet(
            f"font-family:'Microsoft YaHei UI';font-size:10px;color:{COLOR_SUB};"
            "background:transparent;padding:5px 0;"
        )
        self.timeline_layout.addWidget(label)
        self.timeline_layout.addStretch(1)

    def _render_timeline(self, events):
        self._clear_timeline()
        if not events:
            self._set_timeline_message("近期没有服务事件")
            return
        self._wheel_top_spacer = QWidget(self.timeline_content)
        self._wheel_top_spacer.setStyleSheet("background:transparent;")
        self._wheel_top_spacer.setFixedHeight(0)
        self.timeline_layout.addWidget(self._wheel_top_spacer)

        for index, event in enumerate(events):
            row = QWidget(self.timeline_content)
            row_layout = QHBoxLayout(row)
            row_layout.setContentsMargins(18, 2, 4, 0)
            row_layout.setSpacing(6)

            content = QLabel(
                (event["time"].replace("T", " ")[:16] or "最近更新")
                + "\n" + event["title"] + "\n" + event["detail"][:100]
            )
            content.setWordWrap(True)
            content.setStyleSheet(
                f"font-family:'Microsoft YaHei UI';font-size:10px;color:{COLOR_SUB};"
                "background:transparent;"
            )
            row_layout.addWidget(content, 1)
            effect = TimelineWheelEffect(row)
            row.setGraphicsEffect(effect)
            self.timeline_layout.addWidget(row)
            self._timeline_rows.append((row, effect, content))

        self._wheel_bottom_spacer = QWidget(self.timeline_content)
        self._wheel_bottom_spacer.setStyleSheet("background:transparent;")
        self._wheel_bottom_spacer.setFixedHeight(0)
        self.timeline_layout.addWidget(self._wheel_bottom_spacer)
        self.timeline_content.rows = [item[0] for item in self._timeline_rows]
        self.timeline_content.update()
        QTimer.singleShot(0, self._prepare_timeline_wheel)

    def _prepare_timeline_wheel(self):
        """给首尾项预留滚到中轴的空间，避免被滚轮边缘截断。"""
        if not self._timeline_rows:
            return
        viewport_height = self.service_timeline.viewport().height()
        first_row = self._timeline_rows[0][0]
        last_row = self._timeline_rows[-1][0]
        if self._wheel_top_spacer:
            self._wheel_top_spacer.setFixedHeight(
                max(0, int(viewport_height / 2 - first_row.height() / 2 - 8))
            )
        if self._wheel_bottom_spacer:
            self._wheel_bottom_spacer.setFixedHeight(
                max(0, int(viewport_height / 2 - last_row.height() / 2 - 8))
            )
        self._update_timeline_wheel()

    def _update_timeline_wheel(self):
        """模拟 iOS 选时滚轮：中间项清晰，上下项逐步淡出。"""
        viewport = self.service_timeline.viewport()
        center_y = viewport.height() / 2
        opacities = []
        for row, effect, content in self._timeline_rows:
            row_center = row.mapTo(viewport, QPoint(0, 0)).y() + row.height() / 2
            distance = abs(row_center - center_y)
            ratio = min(1.0, distance / max(1, viewport.height() * 0.62))
            direction = (row_center - center_y) / max(1, viewport.height() * 0.62)
            effect.set_position(direction)
            opacities.append(1.0 - ratio * 0.72)
            # 不用选中框或刺眼白字，仅在中央保持正常亮度。
            focus = ratio < 0.16
            content.setStyleSheet(
                "font-family:'Microsoft YaHei UI';font-size:10px;font-weight:400;color:%s;"
                "background:transparent;"
                % (COLOR_TEXT if focus else COLOR_SUB)
            )
        self.timeline_content.row_opacities = opacities
        self.timeline_content.update()
        self._wheel_snap_timer.start(140)

    def _snap_timeline_to_center(self):
        if not self._timeline_rows:
            return
        viewport = self.service_timeline.viewport()
        center_y = viewport.height() / 2
        bar = self.service_timeline.verticalScrollBar()
        closest_row = min(
            self._timeline_rows,
            key=lambda item: abs(
                item[0].mapTo(viewport, QPoint(0, 0)).y() + item[0].height() / 2 - center_y
            ),
        )[0]
        offset = closest_row.mapTo(viewport, QPoint(0, 0)).y() + closest_row.height() / 2 - center_y
        if abs(offset) > 1:
            bar.setValue(bar.value() + int(offset))

    def _on_data(self, payload):
        state, data, error = payload
        if state == "ok":
            self._render(data)
        else:
            self.set_status("error")
            self._set_balance_style(COLOR_CORAL)
            self.balance.setText("--.--")
            self.balance_note.setText("暂时没查到余额")
            self.lbl_topped.setText("充值 --")
            self.lbl_granted.setText("赠送 --")
            self.lbl_time.setText("提示 " + (error or "未知错误")[:22])

    def _render(self, data):
        infos = data.get("balance_infos") or []
        if not infos:
            self.set_status("error")
            self.balance_note.setText("余额信息是空的")
            return

        info = infos[0]
        currency = str(info.get("currency", "CNY"))
        symbol = "¥" if currency == "CNY" else "$"
        try:
            total = float(info.get("total_balance", 0) or 0)
            topped = float(info.get("topped_up_balance", 0) or 0)
            granted = float(info.get("granted_balance", 0) or 0)
        except (TypeError, ValueError):
            total = topped = granted = 0.0

        self._set_balance_style(COLOR_ACCENT)
        self.balance.setText(f"{symbol} {total:.2f}")
        self.balance_note.setText(f"{currency} 可用余额")
        self.lbl_topped.setText(f"充值 {symbol}{topped:.2f}")
        self.lbl_granted.setText(f"赠送 {symbol}{granted:.2f}")
        self.lbl_time.setText(f"更新 {datetime.now().strftime('%H:%M')}")

        # 低余额提醒
        low = total < float(self._cfg.get("low_threshold", 10.0))
        if low and self._cfg.get("low_warn", True):
            self.set_status("ok", "余额偏低")
            self.status_dot.setStyleSheet(
                f"color:{COLOR_AMBER};font-size:12px;background:transparent;"
            )
            self._set_balance_style(COLOR_CORAL)
            self.balance_note.setText(
                f"余额不多啦，低于 {symbol}{float(self._cfg['low_threshold']):.0f} 咯～"
            )
        else:
            self.set_status("ok")
            self._set_balance_style(COLOR_ACCENT)
            self.balance_note.setText(f"{currency} 可用余额")

    # ---------------- 设置 ----------------
    def first_run_guide(self):
        if self._cfg.get("api_key"):
            return
        box = QMessageBox(self)
        box.setWindowTitle("欢迎～")
        box.setIcon(QMessageBox.Information)
        box.setText("🐳 欢迎使用 DeepSeek！\n\n"
                    "第一次使用，先粘贴你的 API Key 吧。\n"
                    "之后随时可以右键悬浮窗重新设置。")
        box.setStandardButtons(QMessageBox.Ok)
        box.button(QMessageBox.Ok).setText("好呀")
        box.exec()
        self.ask_api_key()

    def ask_api_key(self):
        current = (self._cfg.get("api_key") or "").strip()
        text, ok = QInputDialog.getText(
            self,
            "设置 API Key",
            "粘贴你的 DeepSeek API Key：\n"
            "（在 platform.deepseek.com → API Keys 页面获取）",
            QLineEdit.Password,
            current,
        )
        if not ok:
            return
        text = (text or "").strip()
        self._cfg["api_key"] = text
        cfg.save_config(self._cfg)
        if text:
            self.set_status("loading", "看看中")
            self.refresh()
        else:
            self.set_status("no_key")
            self.balance.setText("--.--")
            self.balance_note.setText("还没设置 API Key 哦")

    def set_interval(self, secs):
        self._interval = secs
        self._cfg["refresh_interval"] = secs
        cfg.save_config(self._cfg)
        self._timer.start(secs * 1000)

    def _toggle_low_warn(self, checked):
        self._cfg["low_warn"] = checked
        cfg.save_config(self._cfg)
        self.refresh()
