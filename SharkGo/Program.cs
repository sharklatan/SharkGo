using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Collections;
using System.Net.Http;
using iMobileDevice;
using Newtonsoft.Json;
using ICSharpCode.SharpZipLib.Zip;
using Aspose.Gis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Fleck;
using static SharkGo.Program;


namespace SharkGo
{
    class Program {
        [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
        class EndpointMethod : Attribute {
            public string Name { get; }

            public EndpointMethod(string name) {
                Name = name;
            }
        }

        static bool TryBindListenerOnFreePort(out HttpListener httpListener, out int port)
        {
            const int Port = 49217;

            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://+:{Port}/"); // Prefijo para localhost
                                                            //httpListener.Prefixes.Add($"http://localhost:{Port}/"); // Prefijo para localhost
                                                            //httpListener.Prefixes.Add($"http://+:80/"); // Prefijo para todas las direcciones
                                                            // Configurar los encabezados CORS para permitir todas las solicitudes desde http://sharkgo.sharklatan.com/
            //httpListener.Prefixes.Add($"http://172.30.30.250:{Port}/");


            try
            {
                httpListener.Start();
                port = Port;
                return true;
            }
            catch
            {
                port = 0;
                httpListener = null;
                return false;
            }
        }

        static void OpenBrowser(string url) {
            try {
                Process.Start(url);
            }
            catch {
#if NET
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") {CreateNoWindow = true});
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                    Process.Start("open", url);
                }
                else {
#endif
                    throw;
#if NET
                }
#endif
            }
        }

        static byte[] ReadStream(Stream stream) {
            using (var ms = new MemoryStream()) {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static void SetResponse(HttpListenerContext ctx, string response) {
            using (var sw = new StreamWriter(ctx.Response.OutputStream))
                sw.Write(response);
        }

        static void SetResponse(HttpListenerContext ctx, object response) {
            using (var sw = new StreamWriter(ctx.Response.OutputStream))
                sw.Write(JsonConvert.SerializeObject(response));
        }

        [EndpointMethod("version")]
        static void Version(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            // Write version as response
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            SetResponse(ctx, v.Major + "." + v.Minor);
        }

        [EndpointMethod("home_country")]
        static void HomeCountry(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            // Write current region's english name as response
            SetResponse(ctx, RegionInfo.CurrentRegion.EnglishName);
        }

        private static List<DeviceInformation> Devices = new List<DeviceInformation>();

        [EndpointMethod("get_devices")]
        static void GetDevices(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");



            // Save current devices
            try
            {
                if (Devices != null)
                    lock (Devices)
                        Devices = DeviceInformation.GetDevices();
            }
            catch (Exception e) {
                SetResponse(ctx, new {
                    error = e.Message
                });
            }

            // No devices could be read, sent error
            if (Devices == null) {
                SetResponse(ctx, new {
                    error =
                        "Unable to retrieve connected devices. Ensure iTunes is installed and can detect your device(s)."
                });
            }
            else {
                // Write devices to output
                SetResponse(ctx,
                    Devices.Select(d => new {
                        name = d.Name,
                        display_name = d.ToString(),
                        udid = d.UDID
                    })
                );
                // Obtener la lista de archivos GPX personalizados
                string basePath = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                string directoryPath = Path.Combine(basePath, "gpx", "route", "custom");
                string[] customGpxFiles = Directory.GetFiles(directoryPath, "*.gpx");

                // Enviar la lista de archivos GPX personalizados al cliente a través del WebSocket
                if (webSocketManager != null)
                {
                    string gpxFilesMessage = JsonConvert.SerializeObject(customGpxFiles);
                    webSocketManager.EnviarMensaje(gpxFilesMessage);
                }
                //custom GPX

            }
        }

        class DownloadState {
            public string[] Links { get; }
            public string[] Paths { get; }
            public int CurrentIndex { get; set; }
            public float Progress { get; set; }
            public Exception Error { get; set; }
            public bool Done { get; set; }
            public HttpClient HttpClient { get; }

            public event EventHandler<EventArgs> DownloadCompleted;

            public DownloadState(string[] links, string[] paths) {
                Links = links;
                Paths = paths;
                HttpClient = new HttpClient();
            }

            private void DownloadFileCompleted(Exception e) {
                if (e != null) {
                    Error = e;
                }
                else {
                    try {
                        if (File.Exists(Paths[CurrentIndex])) File.Delete(Paths[CurrentIndex]);
                        File.Move(Paths[CurrentIndex] + ".incomplete", Paths[CurrentIndex]);
                    }
                    catch (Exception ex) {
                        Error = ex;
                        return;
                    }

                    if (CurrentIndex + 1 >= Links.Length) {
                        DownloadCompleted?.Invoke(this, EventArgs.Empty);
                        Done = true;
                    }
                    else {
                        CurrentIndex++;
                        ProcessNext();
                    }
                }
            }

            private void DownloadAsync(Uri uri, string destinationPath) {
                HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                    .ContinueWith(response => {
                        if (response.IsFaulted) {
                            DownloadFileCompleted(response.Exception);
                            return;
                        }

                        var contentLength = response.Result.Content.Headers.ContentLength;
                        response.Result.Content.ReadAsStreamAsync().ContinueWith(result => {
                            if (result.IsFaulted) {
                                DownloadFileCompleted(result.Exception);
                                return;
                            }

                            var stream = result.Result;

                            // Check if progress reporting can be done
                            try {
                                using (var destStream = File.OpenWrite(destinationPath)) {
                                    if (!contentLength.HasValue) {
                                        stream.CopyTo(destStream);
                                        DownloadFileCompleted(null);
                                        return;
                                    }

                                    // Download the file and report ongoing progress
                                    var buffer = new byte[8192];
                                    long totalBytesRead = 0;
                                    int bytesRead;
                                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0) {
                                        destStream.Write(buffer, 0, bytesRead);
                                        totalBytesRead += bytesRead;
                                        Progress = (float)totalBytesRead / contentLength.Value * 100.0f;
                                    }
                                }
                                
                                DownloadFileCompleted(null);
                            }
                            catch (Exception ex) {
                                DownloadFileCompleted(ex);
                            }
                        });
                    });
            }

            private void ProcessNext() {
                Progress = 0;
                var p = Path.GetDirectoryName(Paths[CurrentIndex]);
                if (!string.IsNullOrEmpty(p) && !Directory.Exists(p))
                    Directory.CreateDirectory(p);
                DownloadAsync(new Uri(Links[CurrentIndex]), Paths[CurrentIndex] + ".incomplete");
            }

            public void Start() {
                if (CurrentIndex < Links.Length)
                    ProcessNext();
            }
        }

        static readonly Dictionary<string, DownloadState> Downloads = new Dictionary<string, DownloadState>();

        [EndpointMethod("get_progress")]
        static void GetProgress(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            string version;
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                version = sr.ReadToEnd();

            if (Downloads.TryGetValue(version, out DownloadState state)) {
                if (state.Error != null) {
                    SetResponse(ctx, new { error = state.Error.ToString() });
                }
                else if (state.Done) {
                    SetResponse(ctx, new { done = true });
                }
                else {
                    SetResponse(ctx,
                        new { filename = Path.GetFileName(state.Paths[state.CurrentIndex]), progress = state.Progress });
                }
            }
            else {
                SetResponse(ctx, new { error = "Download state is unrecognised." });
            }
        }

        [EndpointMethod("stop_location")]
        static void StopLocation(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            if (ctx.Request.Headers["Content-Type"] == "application/json") {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) {
                    // Read the JSON body
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());
                    DeviceInformation device;

                    // Find the matching device udid
                    lock (Devices)
                        device = Devices.FirstOrDefault(d => d.UDID == (string) data.udid);

                    // Check if we already have the dependencies
                    if (device == null) {
                        SetResponse(ctx,
                            new {error = "Unable to find the specified device. Are you sure it is connected?"});
                    }
                    else {
                        try {
                            if (DeveloperImageHelper.HasImageForDevice(device, out string[] p)) {
                                device.EnableDeveloperMode(p[0], p[1]);
                                device.StopLocation();
                                SetResponse(ctx, new { success = true });
                            }
                            else {
                                throw new Exception("The developer images for the specified device are missing.");
                            }
                        }
                        catch (Exception e) {
                            SetResponse(ctx, new {error = e.Message});
                        }
                    }
                }
            }
        }

        [EndpointMethod("set_location")]
        static void SetLocation(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            if (ctx.Request.Headers["Content-Type"] == "application/json") {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) {
                    // Read the JSON body
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());
                    DeviceInformation device;

                    // Find the matching device udid
                    lock (Devices)
                        device = Devices.FirstOrDefault(d => d.UDID == (string) data.udid);

                    // Check if we already have the dependencies
                    if (device == null) {
                        SetResponse(ctx,
                            new {error = "Unable to find the specified device. Are you sure it is connected?"});
                    }
                    else {
                        try {
                            // Check if developer mode toggle is visible (on >= iOS 16)
                            if (device.GetDeveloperModeToggleState() ==
                                DeviceInformation.DeveloperModeToggleState.Hidden) {
                                device.EnableDeveloperModeToggle();
                                SetResponse(ctx,
                                    new {
                                        error = "Please turn on Developer Mode first via Settings >> Privacy & Security on your device."
                                    });
                            }
                            // Ensure the developer image exists
                            else if (DeveloperImageHelper.HasImageForDevice(device, out var p)) {
                                device.EnableDeveloperMode(p[0], p[1]);
                                device.SetLocation(new PointLatLng {Lat = data.lat, Lng = data.lng});
                                SetResponse(ctx, new {success = true});
                            }
                            else {
                                throw new Exception("The developer images for the specified device are missing.");
                            }
                        }
                        catch (Exception e) {
                            SetResponse(ctx, new {error = e.Message});
                        }
                    }
                }
            }
        }
        /*
                //Original code 
                [EndpointMethod("set_speed")]
                static void setSpeed(HttpListenerContext ctx)
                {
                    if (ctx.Request.Headers["Content-Type"] == "application/json")
                    {
                        using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        {
                            // Read the JSON body
                            dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());

                            // Set the speed, get the path, and calculate trip statistics
                            pathTraversalSpeed = data.traversalSpeed;
                            gpxFilePath = data.path;

                            ArrayList points = getGPX();
                            if (points.Count == 0)
                            {
                                SetResponse(ctx, new { message = "The file was not found!" });
                                return;
                            }
                            double totalDistance = 0;
                            for (int i = 0; i < points.Count - 1; i++)
                            {
                                PointLatLng first = (PointLatLng)points[i];
                                PointLatLng next = (PointLatLng)points[i + 1];
                                totalDistance += calculateDistanceBetweenLocations(first, next) * 1000; // Convert to meters
                            }
                            double totalTime = totalDistance / pathTraversalSpeed;
                            String messageToSend = String.Format("The path is {0} kilometers long. With the traversal speed of {1} m/s, traversing the path will take {2} minutes.", totalDistance / 1000, pathTraversalSpeed, totalTime / 60);

                            // Send the data back to the frontend
                            SetResponse(ctx, new { message = messageToSend });
                        }
                    }
                }
                //end Original code 
              */
        //MOD code 
         [EndpointMethod("set_speed")]
        static void setSpeed(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            if (ctx.Request.Headers["Content-Type"] == "application/json")
            {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    // Read the JSON body
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());

                    // Set the speed and get the selected GPX file name
                    pathTraversalSpeed = data.traversalSpeed;
                    string selectedFileName = data.path;

                    // Construct the full path for the selected GPX file
                    string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string selectedFilePath = Path.Combine(baseDirectory, "gpx", selectedFileName);
                    gpxFilePath = selectedFilePath;

                    //Debug.WriteLine("Selected File Path: " + gpxFilePath); //console log patch file
                    //Console.WriteLine("Selected Route: " + gpxFilePath);
                    Console.Write("\x1b[2K");
                    Console.Write("Selected Route: " + selectedFileName);

                    //Array points for infinite Route
                    ArrayList points = new ArrayList();
                    // Realiza la operación para obtener nuevos elementos en el ArrayList
                    points = getGPX();
                    if (points.Count == 0)
                    {
                        SetResponse(ctx, new { message = "The file was not found!" });
                        return;
                    }
                    double totalDistance = 0;
                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        PointLatLng first = (PointLatLng)points[i];
                        PointLatLng next = (PointLatLng)points[i + 1];
                        totalDistance += calculateDistanceBetweenLocations(first, next) * 1000; // Convert to meters
                    }
                    double totalTime = totalDistance / pathTraversalSpeed;
                    string messageToSend = String.Format("The path is {0} kilometers long. With the traversal speed of {1} m/s, traversing the path will take {2} minutes.", totalDistance / 1000, pathTraversalSpeed, totalTime / 60);

                    // Send the data back to the frontend
                    SetResponse(ctx, new { message = messageToSend });
                }
            }
        }
        // end Mod file.gpx


        private static bool isWalking = false;
        private static double pathTraversalSpeed = 1.5;
        private static string gpxFilePath = "";
        // Method to stop a "walk"
        [EndpointMethod("stop_walk")]
        static void StopWalk(HttpListenerContext ctx)
        {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");

            if (ctx.Request.Headers["Content-Type"] == "application/json")
            {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    // Read the JSON body
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());
                    DeviceInformation device;

                    // Find the matching device udid
                    lock (Devices)
                        device = Devices.FirstOrDefault(d => d.UDID == (string)data.udid);

                    // Check if we already have the dependencies
                    if (device == null)
                    {
                        SetResponse(ctx,
                            new { error = "Unable to find the specified device. Are you sure it is connected?" });
                    }
                    else
                    {
                        try
                        {
                            if (DeveloperImageHelper.HasImageForDevice(device, out string[] p))
                            {
                                device.EnableDeveloperMode(p[0], p[1]);
                                isWalking = false;
                                Console.Write("\x1b[2K");
                                Console.Write("\x1b[2KThe walk has stopped.");

                                //Console.WriteLine("Stopping a walk.");
                                Thread.Sleep(1000);
                                device.StopLocation();
                                SetResponse(ctx, new { success = true });
                            }
                            else
                            {
                                throw new Exception("The developer images for the specified device are missing.");
                            }
                        }
                        catch (Exception e)
                        {
                            SetResponse(ctx, new { error = e.Message });
                        }
                    }
                }
            }
        }

        // Método para iniciar una "caminata"
        [EndpointMethod("start_walk")]
        static void StartWalk(HttpListenerContext ctx)
        {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");

            if (ctx.Request.Headers["Content-Type"] == "application/json")
            {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    // Leer el cuerpo JSON
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());
                    DeviceInformation device;

                    // Encontrar el dispositivo con udid coincidente
                    lock (Devices)
                        device = Devices.FirstOrDefault(d => d.UDID == (string)data.udid);

                    // Comprobar si ya tenemos las dependencias
                    if (device == null)
                    {
                        SetResponse(ctx,
                            new { error = "Unable to find the specified device. Are you sure it is connected?" });
                    }
                    else
                    {
                        try
                        {
                            // Comprobar si el interruptor de modo desarrollador está visible (en >= iOS 16)
                            if (device.GetDeveloperModeToggleState() ==
                                DeviceInformation.DeveloperModeToggleState.Hidden)
                            {
                                device.EnableDeveloperModeToggle();
                                SetResponse(ctx,
                                    new
                                    {
                                        error = "Please turn on Developer Mode first via Settings >> Privacy & Security on your device."
                                    });
                            }
                            // Asegurarse de que la imagen del desarrollador existe
                            else if (DeveloperImageHelper.HasImageForDevice(device, out var p))
                            {
                                device.EnableDeveloperMode(p[0], p[1]);
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    isWalking = true;
                                    ArrayList points = getGPX();
                                    ArrayList interPoints = new ArrayList();
                                    double speed = pathTraversalSpeed;
                                    double timeBetweenIntervals = 1;

                                    int i = 0; // Índice para controlar los puntos del recorrido
                                    int vecesRepetida = 0; // Variable para contar cuántas veces se ha repetido la ruta

                                    while (isWalking)
                                    {
                                        // Limpiar la línea actual
                                        Console.Write("\x1b[2K");

                                        // Cambiar el color del texto a blanco
                                        Console.Write("\x1b[37m");

                                        Console.CursorLeft = 0; // Mover el cursor al inicio de la líne
                                        // Mostrar el mensaje con colores y conteo
                                        Console.Write("\x1b[37mCalculating a \x1b[31mnew point. \x1b[33m{0}\x1b[37m/\x1b[36m{1} \x1b[32m[{2}%] \x1b[37mRoute repeated \x1b[35m{3} \x1b[37mtimes.", i, points.Count - 1, i * 100 / (points.Count - 1), vecesRepetida);

                                         //Console.CursorLeft = 0; // Mover el cursor al inicio de la línea
                                        //Console.Write($"Calculating a new point. {i}/{points.Count - 1} [{i * 100 / (points.Count - 1)}%] Route repeated {vecesRepetida} times.");

                                        PointLatLng first = (PointLatLng)points[i];
                                        PointLatLng next = (PointLatLng)points[i + 1];
                                        double bearing = calculateBearing(first, next);
                                        double travelledSegmentDistance = 0;
                                        double segmentDistance = calculateDistanceBetweenLocations(first, next) * 1000; // Convertir a metros

                                        while ((travelledSegmentDistance < segmentDistance) && isWalking)
                                        {
                                            double distanceToTravel = speed * timeBetweenIntervals;
                                            PointLatLng nextLocation = calculateDestinationLocation(first, bearing, distanceToTravel / 1000); // La función espera que el argumento de distancia esté en kilómetros
                                            travelledSegmentDistance += calculateDistanceBetweenLocations(first, nextLocation) * 1000; // Convertir a metros

                                            if (travelledSegmentDistance > segmentDistance)
                                            {
                                                break; // Mover al siguiente punto en el gpx
                                            }
                                            device.SetLocation(nextLocation);

                        

                                            webSocketManager.EnviarCoordenadas(nextLocation.Lat, nextLocation.Lng); // Enviar WebSocket

                                            first = nextLocation;
                                            Thread.Sleep((int)(timeBetweenIntervals * 1000));
                                        }

                                        i++; // Avanzar al siguiente punto

                                        if (i >= points.Count - 1)
                                        {
                                            i = 0; // Volver al principio de la lista cuando llegues al final
                                            vecesRepetida++; // Aumentar el contador de repeticiones de ruta
                                        }

                                        device.SetLocation(next);
                                        webSocketManager.EnviarCoordenadas(next.Lat, next.Lng); // Enviar WebSocket
                                        Thread.Sleep((int)(timeBetweenIntervals * 1000));
                                    }

                                    // Limpiar la línea de progreso después de que la caminata haya terminado
                                    Console.CursorLeft = 0;
                                    Console.Write(new string(' ', Console.WindowWidth - 1));
                                    Console.CursorLeft = 0;
                                });

                                SetResponse(ctx, new { success = true });
                            }
                            else
                            {
                                throw new Exception("The developer images for the specified device are missing.");
                            }
                        }
                        catch (Exception e)
                        {
                            SetResponse(ctx, new { error = e.Message });
                        }
                    }
                }
            }
        }





        // Helper functions for the gpx interpolation
        static double degToRad(double deg)
        {
            return (deg * Math.PI / 180);
        }

        static double radToDeg(double rad)
        {
            return (rad * 180 / Math.PI);
        }

        const double EARTH_RADIUS = 6371; // Earth's mean radius in km
        static double calculateBearing(PointLatLng start, PointLatLng end)
        {
            double lat1 = degToRad(start.Lat);
            double lat2 = degToRad(end.Lat);
            double deltaLon = degToRad(end.Lng - start.Lng);

            double y = Math.Sin(deltaLon) * Math.Cos(lat2);
            double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
            double bearing = Math.Atan2(y, x);

            // Since atan2 returns a value between -180 and +180, we need to convert it to 0 - 360 degrees
            return (radToDeg(bearing) + 360) % 360;
        }

        // Calculate the destination point from given point having travelled the given distance (in km), on the given initial bearing (bearing may vary before destination is reached)
        static PointLatLng calculateDestinationLocation(PointLatLng point, double bearing, double distance)
        {
            double angularDistance = distance / EARTH_RADIUS; // convert to angular distance in radians
            bearing = degToRad(bearing); // convert bearing to radians

            double lat1 = degToRad(point.Lat);
            double lon1 = degToRad(point.Lng);

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDistance) + Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1), Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));
            lon2 = (lon2 + 3 * Math.PI) % (2 * Math.PI) - Math.PI; // normalize to -180 - + 180 degrees

            return new PointLatLng { Lat = radToDeg(lat2), Lng = radToDeg(lon2) };
        }

        // Calculate the distance between two points in km
        static double calculateDistanceBetweenLocations(PointLatLng start, PointLatLng end)
        {
            double lat1 = degToRad(start.Lat);
            double lon1 = degToRad(start.Lng);

            double lat2 = degToRad(end.Lat);
            double lon2 = degToRad(end.Lng);

            double deltaLat = lat2 - lat1;
            double deltaLon = lon2 - lon1;

            double a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return EARTH_RADIUS * c;
        }

        static ArrayList getGPX()
        {
            try
            {
                var layerTest = Drivers.Gpx.OpenLayer(gpxFilePath); //System.InvalidOperationException: 'Evaluation limit exceeded: 15 files per hour.'
            }
            catch (System.IO.FileNotFoundException e)
            {
                Console.WriteLine("File not found!");
                Console.WriteLine(e);
                return new ArrayList();
            }
            ArrayList points = new ArrayList();
            var layer = Drivers.Gpx.OpenLayer(gpxFilePath);

            foreach (var feature in layer)
            {
                // Check for point geometry
                if (feature.Geometry.GeometryType == Aspose.Gis.Geometries.GeometryType.Point)
                {
                    // Read points
                    Aspose.Gis.Geometries.Point point = (Aspose.Gis.Geometries.Point)feature.Geometry;
                    points.Add(new PointLatLng { Lat = point.Y, Lng = point.X });
                }
            }

            // Print out the contents of points
            // foreach (PointLatLng point in points) {
            // Console.WriteLine("Longitude: " + point.Lng + " Latitude: " + point.Lat);
            // }
            // for (int i = 0; i < points.Count; i++) {
            //    PointLatLng point = (PointLatLng) points[i];
            //     Console.WriteLine(point.Lat);
            // }

            return points;
        }

        [EndpointMethod("exit")]
        static void Exit(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            SetResponse(ctx, "");
            Environment.Exit(0);
        }

        static void DeveloperImageZipDownloaded(object sender, EventArgs e) {
            var state = (DownloadState) sender;
            var files = state.Paths;

            try {
                foreach (var file in files) {
                    using (var fs = File.OpenRead(file)) {
                        using (var zf = new ZipFile(fs)) {
                            foreach (ZipEntry ze in zf) {
                                if (!ze.IsFile || !ze.Name.Contains("DeveloperDiskImage.dmg"))
                                    continue;
                                using (var ds = zf.GetInputStream(ze)) {
                                    var dest = Path.Combine(Path.GetDirectoryName(file),
                                        ze.Name.Replace('\\', '/').Split('/').Last());
                                    using (var of = File.OpenWrite(dest))
                                        ds.CopyTo(of);
                                }
                            }
                        }
                    }

                    File.Delete(file);
                }
            }
            catch (Exception ex) {
                state.Error = ex;
            }
        }

        [EndpointMethod("has_dependencies")]
        static void HasDepedencies(HttpListenerContext ctx) {
            // Configurar los encabezados CORS para permitir solicitudes desde https://sharkgo.sharklatan.com/
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "https://sharkgo.sharklatan.com");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept");
            if (ctx.Request.Headers["Content-Type"] == "application/json") {
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) {
                    // Read the JSON body
                    dynamic data = JsonConvert.DeserializeObject<dynamic>(sr.ReadToEnd());
                    DeviceInformation device;

                    // Find the matching device udid
                    lock (Devices)
                        device = Devices.FirstOrDefault(d => d.UDID == (string) data.udid);

                    // Check if we already have the dependencies
                    if (device == null) {
                        SetResponse(ctx,
                            new {error = "Unable to find the specified device. Are you sure it is connected?"});
                    }
                    else {
                        // Obtain the status of the depedencies
                        var hasDeps = DeveloperImageHelper.HasImageForDevice(device);
                        var verStr = DeveloperImageHelper.GetSoftwareVersion(device);

                        // Automatically start download if it's missing
                        if (!hasDeps) {
                            var links = DeveloperImageHelper.GetLinksForDevice(device);
                            if (links != null) {
                                bool needsExtraction = links.Any(l =>
                                    l.Item1.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase));
                                var state = new DownloadState(links.Select(t => t.Item1).ToArray(),
                                    links.Select(t => t.Item2).ToArray());
                                if (needsExtraction)
                                    state.DownloadCompleted += DeveloperImageZipDownloaded;
                                lock (Downloads)
                                    if (!Downloads.ContainsKey(verStr))
                                        Downloads[verStr] = state;
                                state.Start();
                            }
                            else {
                                SetResponse(ctx,
                                    new {error = "Your device's iOS version is not supported at this time."});
                                return;
                            }
                        }

                        SetResponse(ctx, new {result = hasDeps, version = verStr});
                    }
                }
            }
        }

        private static WebSocketManager webSocketManager;

        static void Main()
        {
            webSocketManager = new WebSocketManager();
            // Inicia el servidor WebSocket

            string basePath = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            Environment.CurrentDirectory = basePath;

            foreach (SecurityProtocolType protocol in Enum.GetValues(typeof(SecurityProtocolType)))
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= protocol;
                }
                catch
                {
                }
            }

            try
            {
                NativeLibraries.Load();
            }
            catch
            {
                Console.WriteLine("Failed to load necessary files to run SharkGo.");
                return;
            }

            var methods =
                typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Select(mi => new Tuple<MethodInfo, object>(mi,
                        mi.GetCustomAttributes(true).FirstOrDefault(ci => ci is EndpointMethod)))
                    .Where(kvp => kvp.Item2 != null)
                    .ToDictionary(kvp => ((EndpointMethod)kvp.Item2).Name, kvp => kvp.Item1);

            if (!TryBindListenerOnFreePort(out var listener, out var port))
            {
                Console.WriteLine("Failed to initialise SharkGo (no free ports on local system).");
                return;
            }

            try
            {
                // Imprimir una simulación de carga mientras se obtiene la versión actual del ensamblado
                Console.Write("Checking Security: ");
                string[] loadingChars = { "/", "-", "\\", "|" };
                for (int i = 0; i < 8; i++)
                {
                    Console.Write(loadingChars[i % loadingChars.Length]);
                    System.Threading.Thread.Sleep(100); // Simula una pequeña pausa
                    Console.Write("\b");
                }

                Console.WriteLine();
                Console.Write("\nVersion Used: ");

                // Obtener la versión actual del ensamblado
                // Cambiar el color de la versión a azul
                Console.ForegroundColor = ConsoleColor.Blue;
                Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                Console.Write(assemblyVersion);


                // Cambiar el color de la versión a azul
                Console.ForegroundColor = ConsoleColor.White;
                // Restablecer el color y el formato
                Console.ResetColor();
                Console.WriteLine(); // Nueva línea al final
                Console.WriteLine();
                // Verificar la versión antes de continuar
                if (CheckVersion())
                {
                    OpenBrowser($"https://sharkgo.sharklatan.com/");
                    Console.WriteLine("SharkGo is now running at: " + $"{port}");
                    string sHostName = Dns.GetHostName();
                    IPHostEntry ipE = Dns.GetHostEntry(sHostName);
                    IPAddress[] IpA = ipE.AddressList;
                    for (int i = 0; i < IpA.Length; i++)
                    {
                        Console.WriteLine("IP Address {0}: {1} ", i, IpA[i].ToString());
                    }

                    Console.WriteLine("\nPress Ctrl-C to quit (or click the close button).");
                }
            }
            catch
            {
                string url = "https://sharkgo.sharklatan.com/";
#if NET 
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
#endif
                    throw;
#if NET
                }
#endif
            }

            while (true)
            {
                var ctx = listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var methodName = ctx.Request.Url.Segments.Length > 1
                        ? string.Join("", ctx.Request.Url.Segments.Skip(1))
                        : "";
                    if (string.IsNullOrEmpty(methodName))
                        methodName = "main.html";

                    string path;
                    if (File.Exists(path = Path.Combine("Resources",
                        methodName.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        ctx.Response.Headers["Content-Type"] = MimeTypes.GetMimeType(methodName);
                        using (var s = File.OpenRead(path))
                            s.CopyTo(ctx.Response.OutputStream);
                        ctx.Response.Close();
                        return;
                    }
                    else if (methods.TryGetValue(methodName, out MethodInfo method))
                    {
                        try
                        {
                            method.Invoke(null, new object[] { ctx });
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("\n" + e);
                        }

                        try
                        {
                            if (ctx.Response.OutputStream.CanWrite)
                                ctx.Response.OutputStream.Close();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                    else
                    {
                        ctx.Response.Close();
                        return;
                    }
                });
            }
        }

        public class WebSocketManager
        {
            private static List<IWebSocketConnection> sockets = new List<IWebSocketConnection>();
            private static WebSocketServer server;

            public WebSocketManager()
            {
                // Guardar la salida estándar actual
                var originalConsoleOut = Console.Out;

                // Redirigir la salida estándar a null temporalmente para suprimir el mensaje de inicio
                Console.SetOut(TextWriter.Null);

                server = new WebSocketServer("ws://0.0.0.0:49218");

                // Restaurar la salida estándar original
                Console.SetOut(originalConsoleOut);

                server.Start(socket =>
                {
                    socket.OnOpen = () =>
                    {
                        Console.WriteLine("Connected to SharkGo GUI.");
                        sockets.Add(socket);
                    };

                    socket.OnClose = () =>
                    {
                        Console.WriteLine("Disconnected from SharkGo GUI.");
                        sockets.Remove(socket);
                    };

                    socket.OnMessage = message =>
                    {
                        Console.WriteLine($"Command: {message}");
                    };
                });
            }

            public void EnviarCoordenadas(double latitud, double longitud)
            {
                var mensaje = $"{latitud}, {longitud}";
                foreach (var socket in sockets)
                {
                    socket.Send(mensaje);
                }
            }

            public void EnviarMensaje(string mensaje)
            {
                foreach (var socket in sockets)
                {
                    socket.Send(mensaje);
                }
            }
        }

        static bool CheckVersion()
        {
            Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            string versionJsonUrl = "https://raw.githubusercontent.com/sharklatan/SharkGo/master/version.json"; // Reemplaza con la URL real

            try
            {
                using (WebClient client = new WebClient())
                {
                    string json = client.DownloadString(versionJsonUrl);
                    JObject jsonObject = JObject.Parse(json);

                    Version jsonVersion = new Version(jsonObject["version"].ToString());

                    bool enable = jsonObject.Value<int>("Enable") == 1; // Comprobar el valor "Enable"

                    if (enable)
                    {
                        if (assemblyVersion >= jsonVersion)
                        {
                            Console.Write("Versión Online: ");
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine(jsonVersion);
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.ResetColor();
                            Console.WriteLine();
                            return true; // La versión está actualizada
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("New version of SharkGo available. Please update.");
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.ResetColor();
 

                            Console.Write("Versión Online: ");
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine(jsonVersion);
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.ResetColor();
                            Console.WriteLine();
                            System.Threading.Thread.Sleep(3000); // Esperar 3 segundos
                            string updateUrl = "https://sharkgo.sharklatan.com"; // URL fija de actualización

                            OpenBrowser(updateUrl);
                            
                            Environment.Exit(0);
                            return false; // La versión no está actualizada
                            
                        }
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("The SharkGo is disabled. It will be closed.\r\n");
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.ResetColor();
                        return false; // La aplicación está deshabilitada
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error while checking´s: " + ex.Message);
                Console.ForegroundColor = ConsoleColor.White;
                Console.ResetColor();
                Console.WriteLine();
                return false; // Error al verificar la versión
            }
        }



        static void OpenBrowserCheck(string url)
        {
            // Código para abrir el navegador con la URL proporcionada
            try
            {
                Process.Start(url);
            }
            catch
            {
#if NET
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
#endif
                    throw;
#if NET
                }
#endif
            }
        }
        // Puedes utilizar el código existente que ya tienes para esta función.
    
    
} //main
    
}
