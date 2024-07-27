module GirafeAPIFSharp.App

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Newtonsoft.Json
open Microsoft.FSharp.Collections

// ---------------------------------
// Models
// ---------------------------------

type Currency = { symbol: string }

type Amount = 
    { 
        currency: Currency
        value: decimal 
    }

type Product = 
    {
        id: int
        name: string 
        unitPrice: Amount 
    }

type PriceSpecification = 
    { 
        basePrice: Amount
        tax: Amount 
    }

type Message = { Text: string }


// Database PATH

let databasePath = @"C:\Users\practicas\Desktop\ProductDatabaseDb.db"
let connectionString = sprintf "Data Source=%s" databasePath

let createTableIfNotExists () =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <- """
    CREATE TABLE IF NOT EXISTS Product (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Name TEXT NOT NULL,
        UnitPrice REAL NOT NULL,
        Currency TEXT NOT NULL
    );
    """
    command.ExecuteNonQuery() |> ignore

let getProducts () =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <- "SELECT Id, Name, UnitPrice, Currency FROM Product"
    use reader = command.ExecuteReader()
    let products = 
        [ while reader.Read() do
            let id = reader.GetInt32(0)
            let name = reader.GetString(1)
            let unitPrice = reader.GetDecimal(2)
            let currency = reader.GetString(3)
            yield { id = id; name = name; unitPrice = { currency = { symbol = currency }; value = unitPrice } } ]
    products
    
let addProduct (product: Product) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <- "INSERT INTO Product (Name, UnitPrice, Currency) VALUES ($name, $unitPrice, $currency)"
    command.Parameters.AddWithValue("$name", product.name) |> ignore
    command.Parameters.AddWithValue("$unitPrice", product.unitPrice.value) |> ignore
    command.Parameters.AddWithValue("$currency", product.unitPrice.currency.symbol) |> ignore
    command.ExecuteNonQuery() |> ignore

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open Giraffe.ViewEngine

    let layout (content: XmlNode list) =
        html
            []
            [ head
                  []
                  [ title [] [ encodedText "GirafeAPIFSharp" ]
                    link [ _rel "stylesheet"; _type "text/css"; _href "/main.css" ] ]
              body [] content ]

    let partial () = h1 [] [ encodedText "Products from Mathew App" ]

    let productList (products: Product list) =
        let productItems = 
            products |> List.map (fun product ->
                li [] [ encodedText (sprintf "%s: %M %s" product.name product.unitPrice.value product.unitPrice.currency.symbol) ])
        let content = [ partial (); ul [] productItems ]
        layout content

    let index (model: Message) =
        [ partial (); p [] [ encodedText model.Text ] ] |> layout

// ---------------------------------
// Business Logic
// ---------------------------------

let scale factor amount = 
    { 
        currency = amount.currency
        value = factor * amount.value 
    }

let double = scale 2M

let add amount value = 
    { 
        currency = amount.currency
        value = amount.value + value 
    }

let basePrice product quantity = 
    scale (decimal quantity) product.unitPrice

let flatTax (product: Product) amount = 
    scale 0.2M amount

let priceSpecification (tax: Product -> Amount -> Amount) product quantity = 
    let basePrice = basePrice product quantity 
    { 
        basePrice = basePrice
        tax = (tax product basePrice) 
    }

let totalPriceCalculator (tax: Product -> Amount -> Amount) product quantity =
    let spec = priceSpecification tax product quantity
    (quantity, add spec.basePrice spec.tax.value)

// Function to apply a discount
let applyDiscount discount amount =
    let discountAmount = amount.value * (discount / 100M)
    { 
        currency = amount.currency
        value = amount.value - discountAmount 
    }

// Function to calculate final price after discount
let finalPriceCalculator discount product quantity =
    let totalPrice = totalPriceCalculator flatTax product quantity |> snd
    applyDiscount discount totalPrice

// ---------------------------------
// Handlers
// ---------------------------------

let indexHandler (next: HttpFunc) (ctx: HttpContext) =
    let products = getProducts()
    let view = Views.productList products
    htmlView view next ctx

let productHandler (name: string) (next: HttpFunc) (ctx: HttpContext) =
    let currency = { symbol = "USD" }
    let unitPrice = { currency = currency; value = 20M }
    let product = { id = 0; name = name; unitPrice = unitPrice }
    json product next ctx

let addProductHandler (next: HttpFunc) (ctx: HttpContext) =
    task {
        try
            let! product = ctx.BindJsonAsync<Product>()
            addProduct product
            return! text "Product added" next ctx
        with
        | _ -> return! (setStatusCode 400 >=> text "Invalid product data") next ctx
    }

let totalPriceHandler (name: string) (quantity: int) (next: HttpFunc) (ctx: HttpContext) =
    let currency = { symbol = "USD" }
    let unitPrice = { currency = currency; value = 20M }
    let product = { id = 0; name = name; unitPrice = unitPrice }
    let (qty, totalPrice) = totalPriceCalculator flatTax product quantity
    json { name = product.name; unitPrice = totalPrice; id = 0 } next ctx

// Handler to serve HTML page with product list
let productListHandler (next: HttpFunc) (ctx: HttpContext) =
    let products = getProducts()
    let view = Views.productList products
    htmlView view next ctx

// ---------------------------------
// Web App
// ---------------------------------

let webApp =
    choose
        [ GET
          >=> choose [ route "/" >=> indexHandler
                       routef "/hello/%s" (fun name next ctx -> indexHandler next ctx)
                       routef "/product/%s" (fun name next ctx -> productHandler name next ctx)
                       routef "/totalprice/%s/%i" (fun (name, quantity) next ctx -> totalPriceHandler name quantity next ctx)
                       route "/products" >=> productListHandler ]
          POST
          >=> choose [ route "/addProduct" >=> addProductHandler ]
          setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex: Exception) (logger: ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureCors (builder: CorsPolicyBuilder) =
    builder
        .WithOrigins("http://localhost:5000", "https://localhost:5001")
        .AllowAnyMethod()
        .AllowAnyHeader()
    |> ignore

let configureApp (app: IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()

    (match env.IsDevelopment() with
     | true -> app.UseDeveloperExceptionPage()
     | false -> app.UseGiraffeErrorHandler(errorHandler).UseHttpsRedirection())
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services: IServiceCollection) =
    services.AddCors() |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder: ILoggingBuilder) =
    builder.AddConsole().AddDebug() |> ignore

[<EntryPoint>]
let main args =
    createTableIfNotExists()  // Create the table if it doesn't exist
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot = Path.Combine(contentRoot, "WebRoot")

    Host
        .CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(fun webHostBuilder ->
            webHostBuilder
                .UseContentRoot(contentRoot)
                .UseWebRoot(webRoot)
                .Configure(Action<IApplicationBuilder> configureApp)
                .ConfigureServices(configureServices)
                .ConfigureLogging(configureLogging)
            |> ignore)
        .Build()
        .Run()

    0
