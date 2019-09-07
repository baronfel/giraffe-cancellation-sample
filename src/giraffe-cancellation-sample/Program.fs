module GiraffeCancellationSample.App

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open Hopac

// ---------------------------------
// Models
// ---------------------------------

// markers for the successful completion of an associated async-processing-method route
// if true, the request was not cancelled correctly (assuming the request was in fact cancelled by the client)
type Model = 
  { task: bool 
    async: bool 
    job: bool }
  with static member Empty = { task = false; async = false; job = false }

let mutable state = Model.Empty

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open GiraffeViewEngine



    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "giraffe_cancellation_sample" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/main.css" ]
            ]
            body [] content
        ]

    let partial =
        h1 [] [ encodedText "giraffe_cancellation_sample" ]

    let statusTable (model: Model)= 
        let jsGetUrl kind = 
            sprintf """fetch("/%s/2")
                        .then(response => window.location.reload())
                        .catch((err) => console.log(err))
                    """ kind
        
        let row kind status = tr [] [
            td [] [str kind]
            td [] [str (string status)]
            td [] [ button [_onclick (jsGetUrl kind)  ] [ str (sprintf "start %s" kind )] ]
        ]
        
        table [] [
            thead [] [
                tr [] [
                    th [] [ str "method"]
                    th [] [ str "state" ]
                    th [] [ str "fire and cancel" ]
                ]
            ]
            tbody [] [
                row "async" model.async
                row "task" model.task
                row "job" model.job
            ]
        ]
    
    let resetButton = 
        button [_onclick ("""fetch("/reset")
                                .then(() => window.location.reload())
                                .catch((err) => console.log(err))""") ] [str "Reset State"]

    let index model =
        layout [ partial
                 statusTable model
                 resetButton ]

// ---------------------------------
// Web app
// ---------------------------------

let reset () = 
    state <- Model.Empty

let logState (logger: ILogger) = 
    logger.LogWarning(sprintf "State Flags:\n\tTask: %b\n\tAsync: %b\n\tJob: %b" state.task state.async state.job)

let indexHandler = fun next (ctx: HttpContext) -> task { 
    ctx.GetLogger().LogWarning("Rendering Index")
    logState (ctx.GetLogger())
    return! htmlView (Views.index state) next ctx 
}

let taskHandler (seconds: int) = fun next (ctx: HttpContext) -> task {
    do! Task.Delay (TimeSpan.FromSeconds (float seconds))
    state <- { state with task = true }
    ctx.GetLogger().LogWarning("set task marker")
    return! setStatusCode 200 next ctx
}

let asyncHandler (seconds: int) = fun next (ctx: HttpContext) -> 
    async {
        do! Async.Sleep (1000 * seconds)
        state <- { state with async = true }
        ctx.GetLogger().LogWarning("set async marker")
        return! Async.AwaitTask (setStatusCode 200 next ctx)
    } 
    |> Async.StartAsTask

let jobHandler (seconds: int) = fun next (ctx: HttpContext) -> 
    job {
        do! timeOut (TimeSpan.FromSeconds (float seconds))
        state <- { state with job = true }
        ctx.GetLogger().LogWarning("set job marker")
        return! setStatusCode 200 next ctx
    }
    |> Job.toAsync
    |> Async.StartAsTask

let resetHandler = fun next (ctx: HttpContext) -> task {
    reset ()
    ctx.GetLogger().LogWarning("Reset state")
    logState (ctx.GetLogger())
    return! next ctx
}

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=> warbler (fun _ -> indexHandler)
                routef "/task/%i" taskHandler
                routef "/job/%i" jobHandler
                routef "/async/%i" asyncHandler
                route "/reset" >=> resetHandler
            ]
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureCors (builder : CorsPolicyBuilder) =
    builder.WithOrigins("http://localhost:8080")
           .AllowAnyMethod()
           .AllowAnyHeader()
           |> ignore

let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IHostingEnvironment>()
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
        .UseHttpsRedirection()
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services : IServiceCollection) =
    services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder.SetMinimumLevel(LogLevel.Warning)
           .AddConsole()
           .AddDebug() |> ignore

[<EntryPoint>]
let main _ =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(contentRoot)
        .UseIISIntegration()
        .UseWebRoot(webRoot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0