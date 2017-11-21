#r @"../../bin/FSharp.Control.AsyncSeq.dll"
#nowarn "40"
#time "on"

open System
open System.Diagnostics
open FSharp.Control
open System.Collections.Generic
open System.Collections.Concurrent
open System.Runtime.ExceptionServices

[<AutoOpen>]
module AsyncEx =
  
  open System.Threading
  open System.Threading.Tasks

  type Async with

    static member bind (f:'a -> Async<'b>) (a:Async<'a>) : Async<'b> = async.Bind(a, f)

    static member startThreadPoolWithContinuations (a:Async<'a>, ok:'a -> unit, err:exn -> unit, cnc:OperationCanceledException -> unit, ct:CancellationToken) =
      let a = Async.SwitchToThreadPool () |> Async.bind (fun _ -> a)
      Async.StartWithContinuations (a, ok, err, cnc, ct)

    static member chooseChoice (a:Async<'a>) (b:Async<'b>) : Async<Choice<'a * Async<'b>, 'b * Async<'a>>> = async {
      let! ct = Async.CancellationToken
      return!
        Async.FromContinuations <| fun (ok,err,cnc) ->
          let state = ref 0
          let resA = TaskCompletionSource<_>()
          let resB = TaskCompletionSource<_>()
          let inline oka a =
            if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
              ok (Choice1Of2 (a, resB.Task |> Async.AwaitTask))
            else
              resA.SetResult a
          let inline okb b =
            if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
              ok (Choice2Of2 (b, resA.Task |> Async.AwaitTask))
            else
              resB.SetResult b
          let inline err (ex:exn) =
            if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
              err ex
          let inline cnc ex =
            if (Interlocked.CompareExchange(state, 1, 0) = 0) then
              cnc ex
          Async.startThreadPoolWithContinuations (a, oka, err, cnc, ct)
          Async.startThreadPoolWithContinuations (b, okb, err, cnc, ct) }

    static member internal chooseTasks (a:Task<'T>) (b:Task<'U>) : Async<Choice<'T * Task<'U>, 'U * Task<'T>>> =
      async { 
          let! ct = Async.CancellationToken
          let i = Task.WaitAny( [| (a :> Task);(b :> Task) |],ct)
          if i = 0 then return (Choice1Of2 (a.Result, b))
          elif i = 1 then return (Choice2Of2 (b.Result, a)) 
          else return! failwith (sprintf "unreachable, i = %d" i) }

//    static member internal chooseTasksAsync (a:Task<'T>) (b:Task<'U>) : Async<Choice<'T * Task<'U>, 'U * Task<'T>>> =
//      async {
//          let at = a.ContinueWith(fun (t:Task<'T>) -> Choice1Of2 t.Result)
//          let bt = b.ContinueWith(fun (t:Task<'U>) -> Choice2Of2 t.Result)
//          let! i = Task.WhenAny ([| at ; bt |]) |> Async.AwaitTask
//          match i.Result with
//          | Choice1Of2 x -> return Choice1Of2 (x, b)
//          | Choice2Of2 x -> return Choice2Of2 (x, a) }

    static member internal chooseTasksAsync (a:Task<'T>) (b:Task<'U>) : Async<Choice<'T * Task<'U>, 'U * Task<'T>>> =
      async { 
          let ta, tb = a :> Task, b :> Task
          let! i = Task.WhenAny( ta, tb ) |> Async.AwaitTask
          if i = ta then return (Choice1Of2 (a.Result, b))
          elif i = tb then return (Choice2Of2 (b.Result, a)) 
          else return! failwith "unreachable" }

module AsyncSeq =

  let bufferByCountAndTime (bufferSize:int) (timeoutMs:int) (source:AsyncSeq<'T>) : AsyncSeq<'T[]> = 
    if (bufferSize < 1) then invalidArg "bufferSize" "must be positive"
    if (timeoutMs < 1) then invalidArg "timeoutMs" "must be positive"
    asyncSeq {
      let buffer = new ResizeArray<_>()
      use ie = source.GetEnumerator()
      let rec loop remainingItem remainingTime = asyncSeq {
        let! move = 
          match remainingItem with
          | Some rem -> async.Return rem
          | None -> Async.StartChildAsTask(ie.MoveNext())
        let t = Stopwatch.GetTimestamp()
        let! time = Async.StartChildAsTask(Async.Sleep (max 0 remainingTime))
        let! moveOr = Async.chooseTasks move time
        let delta = int ((Stopwatch.GetTimestamp() - t) * 1000L / Stopwatch.Frequency)
        match moveOr with
        | Choice1Of2 (None, _) -> 
          if buffer.Count > 0 then
            yield buffer.ToArray()
        | Choice1Of2 (Some v, _) ->
          buffer.Add v
          if buffer.Count = bufferSize then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop None timeoutMs
          else
            yield! loop None (remainingTime - delta)
        | Choice2Of2 (_, rest) ->
          if buffer.Count > 0 then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop (Some rest) timeoutMs
          else
            yield! loop (Some rest) timeoutMs
      }
      yield! loop None timeoutMs
    }

  let bufferByCountAndTime2 (bufferSize:int) (timeoutMs:int) (source:AsyncSeq<'T>) : AsyncSeq<'T[]> = 
    if (bufferSize < 1) then invalidArg "bufferSize" "must be positive"
    if (timeoutMs < 1) then invalidArg "timeoutMs" "must be positive"
    asyncSeq {
      let buffer = new ResizeArray<_>()
      use ie = source.GetEnumerator()
      let rec loop remainingItem remainingTime = asyncSeq {
        let! move = 
          match remainingItem with
          | Some rem -> async.Return rem
          | None -> Async.StartChildAsTask(ie.MoveNext())
        let t = Stopwatch.GetTimestamp()
        let! time = Async.StartChildAsTask(Async.Sleep (max 0 remainingTime))
        let! moveOr = Async.chooseTasksAsync move time
        let delta = int ((Stopwatch.GetTimestamp() - t) * 1000L / Stopwatch.Frequency)
        match moveOr with
        | Choice1Of2 (None, _) -> 
          if buffer.Count > 0 then
            yield buffer.ToArray()
        | Choice1Of2 (Some v, _) ->
          buffer.Add v
          if buffer.Count = bufferSize then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop None timeoutMs
          else
            yield! loop None (remainingTime - delta)
        | Choice2Of2 (_, rest) ->
          if buffer.Count > 0 then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop (Some rest) timeoutMs
          else
            yield! loop (Some rest) timeoutMs
      }
      yield! loop None timeoutMs
    }

  let bufferByCountAndTime3 (bufferSize:int) (timeoutMs:int) (source:AsyncSeq<'T>) : AsyncSeq<'T[]> = 
    if (bufferSize < 1) then invalidArg "bufferSize" "must be positive"
    if (timeoutMs < 1) then invalidArg "timeoutMs" "must be positive"
    asyncSeq {
      let buffer = new ResizeArray<_>()
      use ie = source.GetEnumerator()
      let rec loop (remainingItem:Async<'T option> option) remainingTime = asyncSeq {
        let move = 
          match remainingItem with
          | Some rem -> rem
          | None -> ie.MoveNext()
        let t = Stopwatch.GetTimestamp()
        let time = Async.Sleep (max 0 remainingTime)
        let! moveOr = Async.chooseChoice move time
        let delta = int ((Stopwatch.GetTimestamp() - t) * 1000L / Stopwatch.Frequency)
        match moveOr with
        | Choice1Of2 (None, _) -> 
          if buffer.Count > 0 then
            yield buffer.ToArray()
        | Choice1Of2 (Some v, _) ->
          buffer.Add v
          if buffer.Count = bufferSize then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop None timeoutMs
          else
            yield! loop None (remainingTime - delta)
        | Choice2Of2 (_, rest) ->
          if buffer.Count > 0 then
            yield buffer.ToArray()
            buffer.Clear()
            yield! loop (Some rest) timeoutMs
          else
            yield! loop (Some rest) timeoutMs
      }
      yield! loop None timeoutMs
    }

//  let bufferByCountAndTime2 (bufferSize:int) (timeoutMs:int) (source:AsyncSeq<'T>) : AsyncSeq<'T[]> = 
//    if (bufferSize < 1) then invalidArg "bufferSize" "must be positive"
//    if (timeoutMs < 1) then invalidArg "timeoutMs" "must be positive"
//    asyncSeq {      
//      use mb = MailboxProcessor.Start (fun _ -> async { return () })
//      let buf = ResizeArray<_>(bufferSize)
//      let rec loop () = async {
//        let! msg = mb.Receive ()
//
//        
//        }
//
//
//      ()
//
//    }



(*

let N = 100000L
let bufferSize = 100
let bufferTime = 1000
let P = 10

//Real: 00:02:03.090, CPU: 00:04:31.515, GC gen0: 917, gen1: 864, gen2: 53
//Real: 00:01:27.512, CPU: 00:03:20.750, GC gen0: 986, gen1: 914, gen2: 59
//Real: 00:01:16.668, CPU: 00:03:16.109, GC gen0: 677, gen1: 607, gen2: 70

*)

(*

let N = 10000L
let bufferSize = 100
let bufferTime = 1000
let P = 100

//Real: 00:02:46.513, CPU: 00:03:01.734, GC gen0: 993, gen1: 927, gen2: 66
//Real: 00:00:52.050, CPU: 00:02:03.796, GC gen0: 996, gen1: 917, gen2: 69
//Real: 00:00:42.190, CPU: 00:01:29.156, GC gen0: 678, gen1: 605, gen2: 72

*)


(*

let N = 100L
let bufferSize = 100
let bufferTime = 1000
let P = 1000

// ???
//Real: 00:00:04.838, CPU: 00:00:09.859, GC gen0: 103, gen1: 97, gen2: 6
//Real: 00:00:03.235, CPU: 00:00:06.515, GC gen0: 66, gen1: 62, gen2: 4


*)


let N = 100L
let bufferSize = 100
let bufferTime = 1000
let P = 1000

let go n = async {
  return!
    AsyncSeq.init n id
    |> AsyncSeq.mapAsync (fun i -> async {
      do! Async.Sleep 0
      return i })
    |> AsyncSeq.bufferByCountAndTime2 bufferSize bufferTime
    |> AsyncSeq.iter ignore
}

Seq.init P id
|> Seq.map (fun _ -> go N)
|> Async.Parallel
|> Async.RunSynchronously