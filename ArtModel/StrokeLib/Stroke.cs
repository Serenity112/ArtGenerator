﻿using ArtModel.Tracing;
using ArtModel.ImageProccessing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArtModel.StrokeLib
{
    public class Stroke : ArtBitmap
    {
        private const byte WhiteColorBorder = 240;

        public Stroke(Bitmap bitmap) : base(bitmap)
        {
            SP = new StrokePropertyCollection<double>();
            PivotPoint = (Width / 2, Height / 2);
        }

        public (int x, int y) PivotPoint { get; private set; }

        public StrokePropertyCollection<double> SP { get; private set; }

        public new Stroke Copy()
        {
            return new Stroke((Bitmap)bitmap.Clone())
            {
                SP = SP,
                PivotPoint = PivotPoint,
            };
        }

        public void Flip(RotateFlipType flipType)
        {
            UnlockBitmap();

            Bitmap mirroredBitmap = (Bitmap)bitmap.Clone();
            mirroredBitmap.RotateFlip(flipType);

           // bitmap.Dispose();
            bitmap = mirroredBitmap;

            LockBitmap();
        }

        public void Resize(double coefficient)
        {
            Math.Clamp(coefficient, 0.001, 100000);

            UnlockBitmap();

            Width = (int)Math.Ceiling(Width * coefficient);
            Height = (int)Math.Ceiling(Height * coefficient);

            Bitmap resizedImage = new Bitmap(Width, Height);
            resizedImage.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);

            using (Graphics g = Graphics.FromImage(resizedImage))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bitmap, new Rectangle(0, 0, Width, Height));
            }

            bitmap.Dispose();
            bitmap = resizedImage;
            PivotPoint = (Width / 2, Height / 2);

            LockBitmap();
        }

        // Что тут происходит:
        // На вход мы подаём абсолютный угол, на который хотим повернуть мазок. В библиотеке все мазки изначально смотрят вверх, поэтому вычитаем из угла PI/2, это relAngle
        // Поворот использует RotateTransform, поэтому мы создаём новую битмапу, заранее расчитывая её размеры, учитывая угол поворота через синусы и косинусы.
        // Это будет битмапа, в которую попадёт наш исходный прямоугольник, когда его повернут на заданный угол.
        // Но поворота битмпаы недостаточно, требуется повернуть точку привязки мазка. Это точка, из которой он будет рисоваться. Она поворачивается через матрицу поворота.
        // Изначально через CalculatePivotPoint() расчитывается исходная точка привзяки в исходной системе координат
        // Далее она смещается в новую систему координат, находящейся в центре прямоугольника.
        // 
        // Я забыл как оно работает :(
        // 
        public void Rotate(double rotationAngle)
        {
            double relAngle = rotationAngle - Math.PI / 2;

            double cosA = Math.Abs(Math.Cos(relAngle));
            double sinA = Math.Abs(Math.Sin(relAngle));
            int newWidth = (int)(cosA * Width + sinA * Height);
            int newHeight = (int)(cosA * Height + sinA * Width);

            //this.Save("C:\\Users\\skura\\source\\repos\\ArtGenerator\\Output\\", "stroke");

            var originalPivot = CalculatePivotPoint();
            originalPivot = (originalPivot.x - Width / 2, originalPivot.y - Height / 2);

            if (SP.GetP(StrokeProperty.Points) >= 2)
            {
                double pivotAngle = GetPivotAngle(originalPivot, (0, 0));
                double pivotRel = pivotAngle + Math.PI / 2;
                double pivotAbs = rotationAngle + Math.PI + pivotRel;
                originalPivot = RotatePoint(originalPivot, pivotAbs - pivotAngle);
            }

            PivotPoint = (originalPivot.x + newWidth / 2, originalPivot.y + newHeight / 2);

            UnlockBitmap();
            Bitmap rotatedBitmap = new Bitmap(newWidth, newHeight);
            rotatedBitmap.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);

            using (Graphics g = Graphics.FromImage(rotatedBitmap))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.FillRectangle(brush, 0, 0, newWidth, newHeight);
                }

                g.TranslateTransform(rotatedBitmap.Width / 2, rotatedBitmap.Height / 2);
                g.RotateTransform((float)(-relAngle * 180 / Math.PI));
                g.TranslateTransform(-bitmap.Width / 2, -bitmap.Height / 2);
                g.DrawImage(bitmap, new Point(0, 0));
            }

            bitmap.Dispose();
            bitmap = rotatedBitmap;

            Width = newWidth;
            Height = newHeight;
            LockBitmap();

            (int x, int y) CalculatePivotPoint()
            {
                StartPointAlign align = SP.GetP(StrokeProperty.Points) == 1 ? StartPointAlign.Center : StartPointAlign.Bottom;

                if (align == StartPointAlign.Center)
                {
                    return (Width / 2, Height / 2);
                }
                else
                {
                    int width = Width;
                    int x1 = 0;
                    int x2 = width;

                    for (int i = 0; i < width; i++)
                    {
                        if (this[i, 3].R <= WhiteColorBorder)
                        {
                            x1 = i;
                            break;
                        }
                    }

                    for (int i = width - 1; i > 0; i--)
                    {
                        if (this[i, 3].R <= WhiteColorBorder)
                        {
                            x2 = i;
                            break;
                        }
                    }
                    return ((x1 + x2) / 2, 0);
                }
            }

            double GetPivotAngle(in (int x, int y) point, in (int x, int y) center)
            {
                (int x, int y) vect = (point.x - center.x, point.y - center.y);
                return Math.Atan2(vect.y, vect.x);
            }

            (int x, int y) RotatePoint(in (int x, int y) point, in double angle)
            {
                double sin = Math.Sin(angle);
                double cos = Math.Cos(angle);

                int xnew = (int)(point.x * cos - point.y * sin);
                int ynew = (int)(point.x * sin + point.y * cos);

                return (xnew, ynew);
            }
        }
    }

    public enum StartPointAlign
    {
        Center = 0,
        Bottom = 1,
    }
}
