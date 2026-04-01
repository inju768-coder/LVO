using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace LivingValleyOpenRouter
{
    internal sealed class DirectInputPopupMenu : IClickableMenu
    {
        private readonly string _npcId;
        private readonly string _displayName;
        private readonly string _openingLine;
        private readonly TextBox _input;
        private readonly ClickableTextureComponent _closeButton;

        private Rectangle _panelBounds;
        private Rectangle _inputBounds;
        private Rectangle _sendButtonBounds;
        private Rectangle _cancelButtonBounds;
        private string _statusText;
        private bool _isSending;

        public DirectInputPopupMenu(string npcId, string displayName, string openingLine)
            : base(Game1.uiViewport.Width / 2 - 320, Game1.uiViewport.Height / 2 - 84, 640, 168, true)
        {
            _npcId = string.IsNullOrWhiteSpace(npcId) ? displayName : npcId;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? _npcId : displayName;
            _openingLine = openingLine;

            _input = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                Text = ""
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(0, 0, 48, 48),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47),
                1f);

            RecalculateBounds();
            _input.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _input;
        }

        private void RecalculateBounds()
        {
            width = 640;
            height = 168;
            xPositionOnScreen = Game1.uiViewport.Width / 2 - width / 2;
            yPositionOnScreen = Game1.uiViewport.Height / 2 - height / 2;

            _panelBounds = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            _inputBounds = new Rectangle(_panelBounds.X + 24, _panelBounds.Y + 26, _panelBounds.Width - 48, 44);
            _cancelButtonBounds = new Rectangle(_panelBounds.Right - 196, _panelBounds.Bottom - 52, 80, 40);
            _sendButtonBounds = new Rectangle(_panelBounds.Right - 104, _panelBounds.Bottom - 52, 80, 40);

            _input.X = _inputBounds.X;
            _input.Y = _inputBounds.Y;
            _input.Width = _inputBounds.Width;
            _input.Height = _inputBounds.Height;
            _closeButton.bounds = new Rectangle(_panelBounds.Right - 40, _panelBounds.Y + 8, 32, 32);
        }

        private void CloseMenu()
        {
            _input.Selected = false;
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = null;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            RecalculateBounds();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y) || _cancelButtonBounds.Contains(x, y))
            {
                Game1.playSound("bigDeSelect");
                CloseMenu();
                return;
            }

            if (_isSending)
                return;

            if (_sendButtonBounds.Contains(x, y))
            {
                _ = SubmitAsync();
                return;
            }

            _input.Selected = _inputBounds.Contains(x, y);
            Game1.keyboardDispatcher.Subscriber = _input.Selected ? _input : null;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_isSending)
            {
                if (key == Keys.Escape)
                    CloseMenu();
                return;
            }

            if (key == Keys.Escape)
            {
                CloseMenu();
                return;
            }

            if (key == Keys.Enter)
            {
                _ = SubmitAsync();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _input.Update();
        }

        private async Task SubmitAsync()
        {
            string playerInput = _input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(playerInput) || string.IsNullOrWhiteSpace(_npcId))
                return;

            _isSending = true;
            _statusText = "보내는 중...";
            _input.Selected = false;
            Game1.keyboardDispatcher.Subscriber = null;

            try
            {
                string clean = await ModEntry.SendDirectChatViaLivingValleyAsync(_npcId, _displayName, playerInput, "player_chat_followup");
                CloseMenu();
                ModEntry.ShowDirectChatResponse(_npcId, clean);
            }
            catch (Exception ex)
            {
                _isSending = false;
                _statusText = $"오류: {ex.Message}";
                _input.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _input;
            }
        }

        public override void draw(SpriteBatch b)
        {
            Game1.drawDialogueBox(_panelBounds.X, _panelBounds.Y, _panelBounds.Width, _panelBounds.Height, false, true);

            _input.Draw(b);

            Point mouse = new Point(Game1.getMouseX(), Game1.getMouseY());
            DrawButton(b, _cancelButtonBounds, "취소", _cancelButtonBounds.Contains(mouse));
            DrawButton(b, _sendButtonBounds, "보내기", _sendButtonBounds.Contains(mouse));

            if (!string.IsNullOrWhiteSpace(_statusText))
                b.DrawString(Game1.smallFont, _statusText, new Vector2(_panelBounds.X + 24, _panelBounds.Bottom - 24), Game1.textColor);

            _closeButton.draw(b);
            drawMouse(b);
        }

        private static void DrawButton(SpriteBatch b, Rectangle bounds, string label, bool hovered)
        {
            IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, hovered ? Color.Wheat : Color.White);
            Vector2 size = Game1.smallFont.MeasureString(label);
            Vector2 pos = new Vector2(bounds.X + (bounds.Width - size.X) / 2f, bounds.Y + (bounds.Height - size.Y) / 2f);
            b.DrawString(Game1.smallFont, label, pos, hovered ? Game1.textColor : new Color(80, 52, 24));
        }
    }
}
