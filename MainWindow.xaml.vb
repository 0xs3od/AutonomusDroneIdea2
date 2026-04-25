Imports NetMQ
Imports NetMQ.Sockets
Imports Newtonsoft.Json
Imports System.Windows.Media.Media3D
Imports System.Windows.Threading

Class MainWindow
    Private _subSocket As SubscriberSocket
    Private _pubSocket As RequestSocket
    Private _uiTimer As DispatcherTimer

    Public Sub New()
        InitializeComponent()
        InitNetwork()
        _uiTimer = New DispatcherTimer()
        _uiTimer.Interval = TimeSpan.FromMilliseconds(20)
        AddHandler _uiTimer.Tick, AddressOf UpdateTelemetry
        _uiTimer.Start()
    End Sub

    Private Sub InitNetwork()
        _subSocket = New SubscriberSocket()
        _subSocket.Connect("tcp://localhost:5556")
        _subSocket.Subscribe("")
        _pubSocket = New RequestSocket()
        _pubSocket.Connect("tcp://localhost:5555")
    End Sub

    ' تتبع الماوس بالدائرة الصفراء
    Private Sub CameraArea_MouseMove(sender As Object, e As MouseEventArgs)
        Dim pos = e.GetPosition(TargetingCanvas)
        MouseReticle.Visibility = Visibility.Visible
        ReticleTransform.X = pos.X - 15
        ReticleTransform.Y = pos.Y - 15
    End Sub

    Private Sub CameraFeed_MouseDown(sender As Object, e As MouseButtonEventArgs)
        Dim pos = e.GetPosition(TargetingCanvas)
        ' إرسال كنسبة مئوية لحل مشكلة الأبعاد
        Dim relX = pos.X / TargetingCanvas.ActualWidth
        Dim relY = pos.Y / TargetingCanvas.ActualHeight

        MouseReticle.Stroke = System.Windows.Media.Brushes.Red ' وميض أحمر عند النقر

        Task.Run(Sub()
                     Try
                         _pubSocket.SendFrame($"LOCK_ON_TARGET|{{""x"": {relX.ToString("F4")}, ""y"": {relY.ToString("F4")}}}")
                         _pubSocket.ReceiveFrameString()
                         Me.Dispatcher.Invoke(Sub() MouseReticle.Stroke = System.Windows.Media.Brushes.Yellow)
                     Catch : End Try
                 End Sub)
    End Sub

    Private Sub UpdateTelemetry(sender As Object, e As EventArgs)
        Dim msg As String = ""
        Dim lastMsg As String = ""

        ' قراءة كل الرسائل الموجودة في الطابور بسرعة وأخذ الأخيرة فقط
        While _subSocket.TryReceiveFrameString(msg)
            lastMsg = msg
        End While

        ' معالجة آخر رسالة وصلت فقط (لإلغاء التأخير)
        If Not String.IsNullOrEmpty(lastMsg) Then
            Try
                Dim data = JsonConvert.DeserializeObject(Of DroneData)(lastMsg)

                ' تحديث المواقع بسلاسة
                InterceptorModel.Transform = New TranslateTransform3D(data.IntX, data.IntY, data.IntZ)
                TargetModel.Transform = New TranslateTransform3D(data.TgtX, data.TgtY, data.TgtZ)

                TxtSuggestion.Text = data.Suggestion
                TxtDistance.Text = $"DISTANCE: {data.Distance:F2}m"
            Catch : End Try
        End If
    End Sub

    Private Sub BtnCommand_Click(sender As Object, e As RoutedEventArgs)
        Dim cmd = CType(sender, Button).Tag.ToString()
        Task.Run(Sub()
                     Try
                         _pubSocket.SendFrame($"{cmd}|{{}}")
                         _pubSocket.ReceiveFrameString()
                     Catch : End Try
                 End Sub)
    End Sub
End Class

Public Class DroneData
    Public Property IntX As Double : Public Property IntY As Double : Public Property IntZ As Double
    Public Property TgtX As Double : Public Property TgtY As Double : Public Property TgtZ As Double
    Public Property Distance As Double : Public Property Suggestion As String
End Class
