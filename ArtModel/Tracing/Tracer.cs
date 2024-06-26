﻿using ArtModel.Core;
using ArtModel.Extensions;
using ArtModel.ImageModel.ImageProccessing;
using ArtModel.ImageProccessing;
using ArtModel.MathLib;
using ArtModel.PhongReflection;
using ArtModel.Statistics;
using ArtModel.StrokeLib;
using ArtModel.Tracing.PathTracing;
using ArtModel.Tracing.PointDeciding;
using System.Collections;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Xml.Linq;

namespace ArtModel.Tracing
{
    public class TracingState
    {
        public static readonly TracingState Prepare = new TracingState() { Locale = "Подготовка" };
        public static readonly TracingState Blurring = new TracingState() { Locale = "Блюр" };
        public static readonly TracingState Tracing = new TracingState() { Locale = "Отрисовка" };
        public static readonly TracingState Shapes = new TracingState() { Locale = "Создание формы/скелетов" };
        public static readonly TracingState Cancelled = new TracingState() { Locale = "Отменено" };

        public string Locale { get; set; } = string.Empty;

        public override string ToString()
        {
            return this.Locale;
        }
    }

    public class Tracer : IEnumerable<(ArtBitmap, ArtBitmap)>
    {
        private ArtBitmap _artificialRender;
        private ArtBitmap _artificialModel;
        private ArtBitmap _originalModel;

        private PathSettings _pathSettings;

        private ArtModelSerializer _artModelSerializer;

        private int _segments;

        private int _points => _segments + 1;

        private int _genetaions;

        private CancellationToken _token;

        public delegate void GeneraionHandler(int status);
        public event GeneraionHandler NotifyGenerationsChange;

        public delegate void StatusHandler(string status);
        public event StatusHandler NotifyStatusChange;

        public CanvasShapeGenerator CanvasShapeGenerator;

        private object _lock = new object();

        public Tracer(ArtBitmap originalCanvas, ArtModelSerializer serealizer, PathSettings pathSettings, CancellationToken cancellationToken)
        {
            _artModelSerializer = serealizer;
            _pathSettings = pathSettings;
            _originalModel = originalCanvas;
            _token = cancellationToken;
            _segments = serealizer.UserInput.Segments;
            _genetaions = serealizer.UserInput.Generations;
            CanvasShapeGenerator = new CanvasShapeGenerator(_originalModel);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<(ArtBitmap, ArtBitmap)> GetEnumerator()
        {
            NotifyStatusChange?.Invoke(TracingState.Prepare.Locale);
            StrokeLibrary strokeLibrary = new StrokeLibrary(_pathSettings.LibraryPath, _artModelSerializer, 1);

            _artificialRender = new ArtBitmap(_originalModel.Width, _originalModel.Height);
            _artificialRender.FillColor(Color.White);

            _artificialModel = new ArtBitmap(_originalModel.Width, _originalModel.Height);
            _artificialModel.FillColor(Color.White);

            // Класс для выборки точек (инициализация)
            IPointDecider decider = new RandomPointDecider();

            // Генерация рисунка по слоям
            for (int gen = 0; gen < _genetaions; gen++)
            {
                NotifyGenerationsChange?.Invoke(gen);
                NotifyStatusChange?.Invoke($"{TracingState.Blurring} | Поколение: {gen}");

                ArtGeneration localData = _artModelSerializer.Generations[gen];
                ArtBitmap blurredOriginalMap = GaussianBlur.ApplyBlur(_originalModel, localData.BlurSigma);
                double[,] blurredBrightnessMap = GaussianBlur.ApplyBlurToBrightnessMap(BrightnessMap.GetBrightnessMap(blurredOriginalMap), localData.BlurSigma);
                (int x, int y) coordinates;

                if (ArtStatistics.Instance.CollectStatistics) { ArtStatistics.Instance.SetGenerationContext(gen, localData); }
                NotifyStatusChange?.Invoke($"{TracingState.Tracing} | Поколение: {gen}");

                switch (gen)
                {
                    // Отрисовка первого слоя. Он рисуется, пока не будет достигнуто N% закрашенности. Рисуется случайно из точек, что ещё не были затрнуты.
                    case 0:
                        decider = new RandomPointDecider(_originalModel, 1);
                        while (decider.DeciderAvaliable())
                        {
                            if (CheckToken())
                            {
                                NotifyStatusChange?.Invoke($"{TracingState.Cancelled} | Поколение: {gen}");
                                yield break;
                            }

                            MakeStroke();
                            decider.PostStroke();

                            yield return (_artificialRender, _artificialModel);
                        }
                        break;
                    // Отрисовка обычных слоёв
                    default:
                        (int w, int h) tileData = (localData.StrokeWidth_Max, localData.StrokeWidth_Max);
                        decider = new WeightedRegionPointDecider(_originalModel, _artificialRender, tileData.w, tileData.h, localData.DispersionTileBound);

                        for (int iteration = 0; iteration < localData.Iterations; iteration++)
                        {
                            if (CheckToken())
                            {
                                NotifyStatusChange?.Invoke($"{TracingState.Cancelled} | Поколение: {gen}");
                                yield break;
                            }

                            try
                            {
                                MakeStroke();
                            }
                            catch (Exception)
                            {
                                break;
                            }

                            decider.PostStroke();

                            yield return (_artificialRender, _artificialModel);
                        }
                        break;
                }


                void MakeStroke()
                {
                    coordinates = decider.GetNewPoint();

                    var tasks = new Task[2];
                    var results = new (TracingResult tracing, Stroke stroke, double dispersion)[2];

                    CreateTask(0, 1);
                    CreateTask(1, -1);

                    void CreateTask(int index, int direction)
                    {
                        int i = index;
                        tasks[i] = Task.Run(() =>
                        {
                            TracingResult tracingResult = GetSegmentedTracePath(localData, blurredOriginalMap, coordinates, blurredBrightnessMap, direction);
                            var stroke = strokeLibrary.ClassifyStroke(tracingResult, localData);
                            double currentresizeCoef = strokeLibrary.CalculateResizeCoefficient(tracingResult, stroke);

                            /*  if (tracingResult.MeanColor.GetAverage() > PhongReflectionModel.BorderColorBrightness)
                              {
                                  var parameters = new PhongReflectionParameters(tracingResult.MeanColor);      
                                  var normal = stroke.NormalMap;
                                  stroke.IsPhongLighted = true;
                                  stroke.PhongModel = PhongReflectionModel.ApplyReflection(stroke, normal, parameters);
                                  stroke.PhongModel.Resize(currentresizeCoef);
                                  stroke.PhongModel.Rotate(tracingResult.MainAbsAngle, Color.White);
                              }*/

                            stroke.Resize(currentresizeCoef);

                            //if (ArtStatistics.Instance.ShapesMap)
                            //{ 
                            stroke.InitShape();
                            stroke.Shape.CalculateShape();
                            //stroke.Shape.GetShape().Save("C:\\Users\\skura\\source\\repos\\ArtGenerator\\Output\\Shapes", $"shape{i}");
                            //}


                            stroke.Rotate(tracingResult.MainAbsAngle, Color.White);

                            results[i].stroke = stroke;
                            results[i].tracing = tracingResult;
                            results[i].dispersion = CalculateStrokeDispersion(stroke, tracingResult, coordinates);
                        });
                    }

                    Task.WaitAll(tasks);

                    var finalResult = results.MinBy(r => r.dispersion)!;
                    finalResult.stroke.Shape.GetShape().Save("C:\\Users\\skura\\source\\repos\\ArtGenerator\\Output\\Shapes", $"shapeFINAL");

                        if (finalResult.dispersion > localData.DispersionTileBound)
                        return;

                    if (ArtStatistics.Instance.CollectStatistics) { ArtStatistics.Instance.AddStroke(finalResult.tracing); }

                    WritePixelsModel(finalResult.tracing.Coordinates, finalResult.tracing.MeanColor, decider);
                    WritePixelsRender(finalResult.stroke, finalResult.tracing, coordinates, decider);
                }

                yield return (_artificialRender, _artificialModel);
            }
        }

        private bool CheckToken()
        {
            return _token.IsCancellationRequested;
        }

        private TracingResult GetSegmentedTracePath(ArtGeneration genData, ArtBitmap bitmap, (int x, int y) startingPoint, double[,] brightnessMap, int direction)
        {
            double tolerance = genData.DispersionStrokeBound;
            int lenMin = genData.StrokeLength_Min;
            int lenMax = genData.StrokeLength_Max;

            TracingResult tracingResult = new TracingResult();
            tracingResult.SP.SetP(StrokeProperty.Points, 1);

            CircleTracingResult roi = CircleTracer.TraceIterative(genData, bitmap, startingPoint);
            MeanColorCalculator segmentedCalculator = roi.Calculator;
            HashSet<(int x, int y)> segmentedPathCoordinates = roi.Coordinates;
            tracingResult.SP.SetP(StrokeProperty.Width, roi.Width);

            Dictionary<int, (int x, int y)> pathPoints = new() { { 1, startingPoint } };
            Dictionary<int, double> pathSegmentLengths = new();

            double segmentedLength = 0;

            // Условие, чтобы досрочно прервать формирование мазка
            bool cancel_stroke_path = false;

            // Построение каждого сегмента
            // Начинаем строить вторую точку, одна и так есть 100%
            for (int segmentPoint = 2; segmentPoint <= _points; segmentPoint++)
            {
                if (cancel_stroke_path) { break; }

                var currPoint = pathPoints[segmentPoint - 1];

                double newAngle = 0;
                switch (segmentPoint)
                {
                    case 2:
                        newAngle = NormalAngle(brightnessMap[currPoint.y, currPoint.x]);
                        if (direction == -1) { newAngle = PiAngle(newAngle); }
                        tracingResult.MainAbsAngle = newAngle;
                        break;
                    default:
                        var prevPoint = pathPoints[segmentPoint - 2];
                        var angles = GetNewAngle(brightnessMap[currPoint.y, currPoint.x], prevPoint, currPoint);

                        if (angles.vectorsAngle % Math.Tau >= 2.8)
                        {
                            cancel_stroke_path = true;
                            break;
                        }

                        newAngle = angles.absAngle;
                        var angle1 = (angles.vectorsAngle / Math.PI * 180);
                        angle1 *= angles.leftDirection ? -1 : 1;
                        tracingResult.SP.SetP(StrokeProperty.Angle1, angle1);
                        break;
                }

                if (cancel_stroke_path) { break; }

                Task[] tasks = new Task[lenMax - lenMin + 1];
                TracingPath[] tracingPaths = new TracingPath[lenMax - lenMin + 1];
                for (int len = lenMin; len <= lenMax; len++)
                {
                    int index = len - lenMin;
                    int len_i = len;

                    tasks[index] = Task.Run(() =>
                    {
                        (int x, int y) offsetedPoint = VectorMath.PointOffsetClamp(currPoint, newAngle, len_i, bitmap.Width - 1, bitmap.Height - 1);
                        TracingPath path = LinearPathTracer.GetPath(bitmap, currPoint, offsetedPoint, segmentedPathCoordinates, segmentedCalculator, roi.Width);
                        path.Length = len_i;
                        path.EndPoint = offsetedPoint;
                        tracingPaths[index] = path;
                    });
                }

                Task.WaitAll(tasks);

                cancel_stroke_path = true;
                for (int i = tracingPaths.Length - 1; i >= 0; i--)
                {
                    TracingPath tpath = tracingPaths[i];

                    // Найден путь, у которого дисперсия допустимая
                    if (tpath.Dispersion <= tolerance)
                    {
                        // Путь является минимально возможным - значит окончить мазок
                        if (tpath.Length <= lenMin)
                        {
                            cancel_stroke_path = true;
                            break;
                        }
                        else
                        {
                            cancel_stroke_path = false;

                            segmentedLength += tpath.Length;
                            tracingResult.Dispersion = tpath.Dispersion;
                            tracingResult.SP.SetP(StrokeProperty.Length, segmentedLength);
                            tracingResult.SP.SetP(StrokeProperty.LtoW, segmentedLength / roi.Width);
                            tracingResult.SP.SetP(StrokeProperty.Fraction, (segmentedLength - tpath.Length) * 100 / segmentedLength);

                            segmentedCalculator = tpath.Calculator;
                            segmentedPathCoordinates.UnionWith(tpath.Coordinates);

                            tracingResult.SP.SetP(StrokeProperty.Points, segmentPoint);

                            pathPoints.Add(segmentPoint, tpath.EndPoint);
                            pathSegmentLengths.Add(segmentPoint - 1, tpath.Length);
                            break;
                        }
                    }
                }
            }

            tracingResult.Path = pathPoints;
            tracingResult.Coordinates = segmentedPathCoordinates;
            tracingResult.MeanColor = segmentedCalculator.GetMeanColor();

            return tracingResult;

            (double absAngle, double vectorsAngle, bool leftDirection) GetNewAngle(double absAngle, (int x, int y) prevPoint, (int x, int y) currPoint)
            {
                // Взяли нормаль к углу
                absAngle = NormalAngle(absAngle);

                // Нашли 3ю точку по вектору
                (int x, int y) newPoint = VectorMath.PointOffset(currPoint, absAngle, 10);

                // Относительный угол между тремя точками, от 0 до PI
                double angleBetwreenVectors = VectorMath.AngleBy3Points(prevPoint, currPoint, newPoint);

                // Если относительный угол между сегментами мазка оказался острым, разворачиваем на 180 градусов всё
                if (angleBetwreenVectors < Math.PI / 2)
                {
                    angleBetwreenVectors = Math.PI - angleBetwreenVectors;
                    absAngle += Math.PI;
                    newPoint = VectorMath.PointOffset(currPoint, absAngle, 10);
                }

                // На этом этапе у нас точно есть правильный расчётный сегмента из трёх точек

                bool left = VectorMath.LeftOrRight(prevPoint, currPoint, newPoint) == 1;

                return (absAngle, angleBetwreenVectors, left);
            }

            double NormalAngle(double angle)
            {
                return (angle + Math.PI / 2) % (Math.Tau);
            }

            double PiAngle(double angle)
            {
                return (angle + Math.PI) % (Math.Tau);
            }
        }

        private double CalculateStrokeDispersion(Stroke stroke, TracingResult tracingResult, (int x, int y) globalPoint)
        {
            globalPoint = VectorMath.PointOffsetClamp(globalPoint, (tracingResult.MainAbsAngle + Math.PI), tracingResult.SP.GetP(StrokeProperty.Width) / 2, _artificialRender.Width, _artificialRender.Height);
            Color color = tracingResult.MeanColor;
            double disperson = 0;
            int counter = 0;

            for (int x = 0; x < stroke.Width; x++)
            {
                for (int y = 0; y < stroke.Height; y++)
                {
                    int globalX = globalPoint.x - stroke.PivotPoint.x + x;
                    int globalY = globalPoint.y - stroke.PivotPoint.y + y;

                    if (_artificialRender.IsInside(globalX, globalY))
                    {
                        byte strokeAlpha = stroke[x, y].R;
                        if (strokeAlpha < Stroke.BLACK_BORDER_MEDIUM)
                        {
                            counter++;
                            Color renderColor = GraphicsMath.CalculateAlpha(_artificialRender[globalX, globalY], color, (255.0 - strokeAlpha) / 255.0);
                            Color originalColor = _originalModel[globalX, globalY];
                            disperson += GraphicsMath.CalculateSquaredEuclideanDistance(renderColor, originalColor);
                        }
                    }
                }
            }

            if (counter == 0) counter = 1;

            return disperson / counter;
        }

        private void WritePixelsModel(HashSet<(int x, int y)> coordonates, Color color, IPointDecider decider = null)
        {
            foreach (var c in coordonates)
            {
                _artificialModel[c.x, c.y] = color;
            }
        }

        private void WritePixelsRender(Stroke stroke, TracingResult tracingResult, (int x, int y) globalPoint, IPointDecider decider = null)
        {
            globalPoint = VectorMath.PointOffsetClamp(globalPoint, (tracingResult.MainAbsAngle + Math.PI), tracingResult.SP.GetP(StrokeProperty.Width) / 2, _artificialRender.Width, _artificialRender.Height);

            Func<int, int, Color> GetColor = stroke.IsPhongLighted ? (x, y) => { return stroke.PhongModel[x, y]; } : (x, y) => { return tracingResult.MeanColor; };

            if (ArtStatistics.Instance.ShapesMap)
            {
                // CanvasShapeGenerator.OpenNewStroke(tracingResult.MeanColor);
                // CanvasShapeGenerator.AddStrokeSkelet(tracingResult.Path);
            }

            for (int x = 0; x < stroke.Width; x++)
            {
                for (int y = 0; y < stroke.Height; y++)
                {
                    int globalX = globalPoint.x - stroke.PivotPoint.x + x;
                    int globalY = globalPoint.y - stroke.PivotPoint.y + y;

                    if (_artificialRender.IsInside(globalX, globalY))
                    {
                        // Наложение обычного мазка
                        var strokeAlpha = stroke[x, y].R;
                        if (strokeAlpha < Stroke.BLACK_BORDER_MEDIUM)
                        {
                            _artificialRender[globalX, globalY] = GraphicsMath.CalculateAlpha(_artificialRender[globalX, globalY], GetColor(x, y), (255.0 - strokeAlpha) / 255.0);

                            decider?.PointCallback((globalX, globalY));
                        }

                        // Запись контура
                        if (ArtStatistics.Instance.ShapesMap)
                        {
                            //if (stroke.Shape?[x, y].G > 0) CanvasShapeGenerator.AddPixel((globalX, globalY), ShapeType.Filler);
                            //if (stroke.Shape?[x, y].R > 0) CanvasShapeGenerator.AddPixel((globalX, globalY), ShapeType.Shape);
                        }
                    }
                }
            }
        }

        public (ArtBitmap bitmap, double dispersion) CreateErrorMap()
        {
            double dispersion = 0;
            var errorMap = new ArtBitmap(_originalModel.Width, _originalModel.Height);

            for (int x = 0; x < _originalModel.Width; x++)
            {
                for (int y = 0; y < _originalModel.Height; y++)
                {
                    var col1 = _originalModel[x, y];
                    var col2 = _artificialRender[x, y];

                    dispersion += GraphicsMath.CalculateSquaredEuclideanDistance(col1, col2);

                    double errR = 1 - (Math.Abs(col1.R - col2.R) * 1.0 / 255);
                    double errG = 1 - (Math.Abs(col1.G - col2.G) * 1.0 / 255);
                    double errB = 1 - (Math.Abs(col1.B - col2.B) * 1.0 / 255);

                    int R = (int)(255 * errR);
                    int G = (int)(255 * errG);
                    int B = (int)(255 * errB);

                    var coldiff = Color.FromArgb(R, G, B);
                    errorMap[x, y] = coldiff;
                }
            }

            return (errorMap, dispersion);
        }
    }
}
