using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace LivingValleyOpenRouter
{
    internal sealed class VanillaConversationMenu : IClickableMenu
    {
        private enum ConversationState
        {
            Choose,
            Compose,
            Waiting
        }

        private readonly NPC _npc;
        private readonly string _npcId;
        private readonly string _displayName;
        private readonly string _openingLine;
        private readonly TextBox _input;
        private readonly ClickableTextureComponent _closeButton;

        private ConversationState _state = ConversationState.Choose;
        private string _displayText;
        private string _statusText;
        private bool _usedOpeningContext;
        private bool _hasSentPlayerMessage;

        private Rectangle _dialogueBounds;
        private Rectangle _textBounds;
        private Rectangle _portraitBounds;
        private Rectangle _choicePromptBounds;
        private Rectangle _talkButtonBounds;
        private Rectangle _leaveButtonBounds;
        private Rectangle _popupBounds;
        private Rectangle _inputBounds;
        private Rectangle _sendButtonBounds;

        public VanillaConversationMenu(NPC npc, string npcId, string openingLine)
            : base(Game1.uiViewport.Width / 2 - 420, Game1.uiViewport.Height / 2 - 180, 840, 360, true)
        {
            _npc = npc;
            _npcId = string.IsNullOrWhiteSpace(npcId) ? npc?.Name ?? "NPC" : npcId;
            _displayName = npc?.displayName ?? _npcId;
            _openingLine = string.IsNullOrWhiteSpace(openingLine)
                ? $"{_displayName} looks at you."
                : openingLine.Trim();
            _displayText = _openingLine;

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
        }

        private void RecalculateBounds()
        {
            width = 840;
            height = 360;
            xPositionOnScreen = Game1.uiViewport.Width / 2 - width / 2;
            yPositionOnScreen = Game1.uiViewport.Height / 2 - height / 2;

            _dialogueBounds = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            _textBounds = new Rectangle(_dialogueBounds.X + 36, _dialogueBounds.Y + 34, 440, 208);
            _portraitBounds = new Rectangle(_dialogueBounds.Right - 234, _dialogueBounds.Y + 30, 180, 180);
            _choicePromptBounds = new Rectangle(_dialogueBounds.X + 36, _dialogueBounds.Bottom - 118, 520, 28);
            _talkButtonBounds = new Rectangle(_dialogueBounds.X + 36, _dialogueBounds.Bottom - 84, 250, 48);
            _leaveButtonBounds = new Rectangle(_dialogueBounds.X + 304, _dialogueBounds.Bottom - 84, 250, 48);

            _popupBounds = new Rectangle(_dialogueBounds.X + 120, _dialogueBounds.Y + 110, 600, 128);
            _inputBounds = new Rectangle(_popupBounds.X + 26, _popupBounds.Y + 46, 420, 44);
            _sendButtonBounds = new Rectangle(_popupBounds.Right - 126, _popupBounds.Y + 42, 88, 52);

            _input.X = _inputBounds.X;
            _input.Y = _inputBounds.Y;
            _input.Width = _inputBounds.Width;
            _input.Height = _inputBounds.Height;
            _closeButton.bounds = new Rectangle(_dialogueBounds.Right - 56, _dialogueBounds.Y + 10, 48, 48);
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
            if (_closeButton.containsPoint(x, y))
            {
                CloseMenu();
                return;
            }

            if (_state == ConversationState.Choose)
            {
                if (_talkButtonBounds.Contains(x, y))
                {
                    Game1.playSound("smallSelect");
                    _state = ConversationState.Compose;
                    _input.Selected = true;
                    Game1.keyboardDispatcher.Subscriber = _input;
                    return;
                }

                if (_leaveButtonBounds.Contains(x, y))
                {
                    Game1.playSound("bigDeSelect");
                    CloseMenu();
                }

                return;
            }

            if (_state == ConversationState.Compose)
            {
                _input.Update();
                if (_sendButtonBounds.Contains(x, y))
                {
                    _ = SubmitPlayerInputAsync();
                    return;
                }

                _input.Selected = _inputBounds.Contains(x, y);
                Game1.keyboardDispatcher.Subscriber = _input.Selected ? _input : null;
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_state == ConversationState.Waiting)
            {
                if (key == Keys.Escape)
                    CloseMenu();
                return;
            }

            if (_state == ConversationState.Compose)
            {
                if (key == Keys.Escape)
                {
                    _state = ConversationState.Choose;
                    _input.Selected = false;
                    Game1.keyboardDispatcher.Subscriber = null;
                    return;
                }

                if (key == Keys.Enter)
                {
                    _ = SubmitPlayerInputAsync();
                    return;
                }
            }

            if (_state == ConversationState.Choose && key == Keys.Escape)
            {
                CloseMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            if (_state == ConversationState.Compose)
                _input.Update();
        }

        private async Task SubmitPlayerInputAsync()
        {
            string playerInput = _input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(playerInput))
                return;

            _state = ConversationState.Waiting;
            _statusText = "상대의 대답을 기다리는 중...";
            _input.Selected = false;
            Game1.keyboardDispatcher.Subscriber = null;

            try
            {
                string contextBlock = null;
                if (!_usedOpeningContext)
                {
                    contextBlock =
                        "[VANILLA_DIALOGUE_CONTEXT]\n" +
                        $"Before the player spoke, {_displayName} had just said: {_openingLine}\n" +
                        "Continue naturally from that line. The player's first typed message is their real first reply.";
                }

                string raw = await ModEntry.AIClient.SendChatAsync(_npcId, playerInput, contextBlock);
                string clean = ModEntry.AIClient.CleanNpcResponse(raw);

                _usedOpeningContext = true;
                _hasSentPlayerMessage = true;
                _displayText = string.IsNullOrWhiteSpace(clean) ? "..." : clean;
                _statusText = null;
                _input.Text = "";
                _state = ConversationState.Choose;
            }
            catch (Exception ex)
            {
                _statusText = $"오류: {ex.Message}";
                _state = ConversationState.Compose;
                _input.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _input;
            }
        }

        public override void draw(SpriteBatch b)
        {
            Game1.drawDialogueBox(_dialogueBounds.X, _dialogueBounds.Y, _dialogueBounds.Width, _dialogueBounds.Height, false, true);

            DrawWrappedText(b, _displayText, _textBounds, new Color(86, 52, 20));
            DrawPortraitPanel(b);
            DrawNamePlate(b);

            if (_state == ConversationState.Choose || _state == ConversationState.Waiting)
                DrawChoiceState(b);
            else if (_state == ConversationState.Compose)
                DrawComposePopup(b);

            if (!string.IsNullOrWhiteSpace(_statusText))
                b.DrawString(Game1.smallFont, _statusText, new Vector2(_dialogueBounds.X + 36, _dialogueBounds.Bottom - 26), Game1.textColor);

            _closeButton.draw(b);
            drawMouse(b);
        }

        private void DrawPortraitPanel(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(b, _portraitBounds.X, _portraitBounds.Y, _portraitBounds.Width, _portraitBounds.Height, Color.White);

            Texture2D portrait = _npc?.Portrait;
            if (portrait == null)
                return;

            Rectangle source = new Rectangle(0, 0, Math.Min(64, portrait.Width), Math.Min(64, portrait.Height));
            var target = new Rectangle(_portraitBounds.X + 18, _portraitBounds.Y + 18, _portraitBounds.Width - 36, _portraitBounds.Height - 36);
            b.Draw(portrait, target, source, Color.White);
        }

        private void DrawNamePlate(SpriteBatch b)
        {
            Vector2 size = Game1.dialogueFont.MeasureString(_displayName);
            var namePos = new Vector2(_portraitBounds.X + (_portraitBounds.Width - size.X) / 2f, _portraitBounds.Bottom + 8);
            b.DrawString(Game1.dialogueFont, _displayName, namePos, new Color(80, 52, 24));
        }

        private void DrawChoiceState(SpriteBatch b)
        {
            string prompt = _hasSentPlayerMessage ? "계속 이야기할까요?" : "어떻게 반응할까요?";
            b.DrawString(Game1.smallFont, prompt, new Vector2(_choicePromptBounds.X, _choicePromptBounds.Y), new Color(92, 54, 24));

            if (_state == ConversationState.Waiting)
                return;

            Point mouse = new Point(Game1.getMouseX(), Game1.getMouseY());
            DrawButton(b, _talkButtonBounds, "대화하기", _talkButtonBounds.Contains(mouse));
            DrawButton(b, _leaveButtonBounds, "지나가기", _leaveButtonBounds.Contains(mouse));
        }

        private void DrawComposePopup(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(b, _popupBounds.X, _popupBounds.Y, _popupBounds.Width, _popupBounds.Height, Color.White);
            b.DrawString(Game1.smallFont, $"{_displayName}에게 뭐라고 말할까요?", new Vector2(_popupBounds.X + 24, _popupBounds.Y + 16), new Color(80, 52, 24));

            _input.Draw(b);

            Point mouse = new Point(Game1.getMouseX(), Game1.getMouseY());
            DrawButton(b, _sendButtonBounds, "보내기", _sendButtonBounds.Contains(mouse));
        }

        private static void DrawButton(SpriteBatch b, Rectangle bounds, string label, bool hovered)
        {
            IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, hovered ? Color.Wheat : Color.White);
            Vector2 size = Game1.smallFont.MeasureString(label);
            var pos = new Vector2(bounds.X + (bounds.Width - size.X) / 2f, bounds.Y + (bounds.Height - size.Y) / 2f);
            b.DrawString(Game1.smallFont, label, pos, hovered ? Game1.textColor : new Color(80, 52, 24));
        }

        private static void DrawWrappedText(SpriteBatch b, string text, Rectangle bounds, Color color)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string[] lines = ChatUiPatch.WrapTextKorean(Game1.dialogueFont, text, bounds.Width);
            float y = bounds.Y;
            foreach (string line in lines.Take(8))
            {
                b.DrawString(Game1.dialogueFont, line, new Vector2(bounds.X, y), color);
                y += Game1.dialogueFont.LineSpacing + 10;
            }
        }
    }
}
