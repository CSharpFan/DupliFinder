using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;

namespace DupliFinder
{
    class ThreadQueue
    {
        private WorkItemQueue m_WorkQueue;
        private WaitCallback oWaitCallback;

        private int m_ItemsProcessing;
        private int m_NoParallelThreads;

        private object m_Lock_WorkItem = new object();

        /// <summary>
        /// Configures how many concurrent threads can be dispatching the workerqueue, be warned, you construct
        /// many of these classes in your application, all configured with larger numbers. Note: you only get ~20 threads per processor
        /// anyhow.
        /// 
        /// Then you will create contention between each of these objects and the threadpool, especially,
        /// if you then start blocking waiting for workitems to be completed.
        /// 
        /// I generally use values between 1-10, depending on why i am multithreading.
        /// 
        /// If a value is entered greater than the possible number of threads the computer can support, then
        /// it is set to the maximum for that computer.
        /// </summary>
        /// <param name="NoParallelThreads">Configures how many concurrent threads can be dispatching the workerqueue</param>
        public ThreadQueue(int NoParallelThreads)
        {
            #region Configure Thread Count
            int iWorkerThreads = 0;
            int iCompletionPortThreads = 0;

            ThreadPool.GetMaxThreads(out iWorkerThreads, out iCompletionPortThreads);

            m_NoParallelThreads = NoParallelThreads > iWorkerThreads ? iWorkerThreads : NoParallelThreads;

            Debug.WriteLine(String.Format("PrioritisedWorkerQueue.Constr: Requested:{0} Availible:{1}", NoParallelThreads, m_NoParallelThreads));
            #endregion

            m_WorkQueue = new WorkItemQueue();
            m_ItemsProcessing = 0;

            oWaitCallback = new WaitCallback(InvokeWaitHandleDelegate);

            //if (ThreadQueueMonitor.Instance.ThreadPoolCount != null)
            //    ThreadQueueMonitor.Instance.ThreadPoolCount.Increment();
        }

        ~ThreadQueue()
        {
            //if (ThreadQueueMonitor.Instance.ThreadPoolCount != null)
            //    ThreadQueueMonitor.Instance.ThreadPoolCount.Decrement();
        }

        /// <summary>
        /// Place a new waitcallback pointer to you method on the queue. Null will be passed as
        /// the parameter to the method.
        /// </summary>
        /// <param name="Method">WaitCallback to your method void METHOD(object)</param>
        public void QueueUserWorkItem(WaitCallback Method)
        {
            QueueUserWorkItem(Method, null);
        }

        /// <summary>
        /// Place a new waitcallback pointer to you method on the queue. The second parameter will be passed
        /// to your method.
        /// </summary>
        /// <param name="Method">WaitCallback to your method void METHOD(object)</param>
        /// <param name="State">Parameter that will be passed to your method as object</param>
        public void QueueUserWorkItem(WaitCallback Method, Object State)
        {
            QueueUserWorkItem(Method, State, ThreadPriority.Normal);
        }

        /// <summary>
        /// Place a new waitcallback pointer to you method on the queue. The second parameter will be passed
        /// to your method.
        /// 
        /// The priority is used to sort the pending workitems, prior to them being placed on the threadpool,
        /// once they are being executed that are running with the same priority.
        /// </summary>
        /// <param name="Method">WaitCallback to your method void METHOD(object)</param>
        /// <param name="State">Parameter that will be passed to your method as object</param>
        /// <param name="Priority">System.Threading.ThreadPriority of this method</param>
        public void QueueUserWorkItem(WaitCallback Method, Object State, ThreadPriority Priority)
        {
            QueueUserWorkItem(Method, State, Priority, Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Place a new waitcallback pointer to you method on the queue. The second parameter will be passed
        /// to your method.
        /// 
        /// The priority is used to sort the pending workitems, prior to them being placed on the threadpool,
        /// once they are being executed that are running with the same priority.
        /// 
        /// If a key is shared between 2 workitems, and one is already on the queue when another is attempted to be
        /// placed on the queue, then the second one isnt place upon the queue.
        /// 
        /// This is a shortcutting piece of logic.
        /// </summary>
        /// <param name="Method">WaitCallback to your method void METHOD(object)</param>
        /// <param name="State">Parameter that will be passed to your method as object</param>
        /// <param name="Priority">System.Threading.ThreadPriority of this method</param>
        /// <param name="ItemKey">Unique identifier for the work item</param>
        public void QueueUserWorkItem(WaitCallback Method, Object State, ThreadPriority Priority, object ItemKey)
        {
            WorkItem oPThreadWorkItem = new WorkItem(Method, State, Priority, ItemKey);

            QueueUserWorkItem(oPThreadWorkItem);
        }

        /// <summary>
        /// Private method that queues the work item, and ensures that a thread is executing to process the queue.
        /// 
        /// This method starts the dispatching of the work, however, subsequent invocations will be spawned by the 
        /// threadpool itself.
        /// </summary>
        /// <param name="oPThreadWorkItem"></param>
        private void QueueUserWorkItem(WorkItem oPThreadWorkItem)
        {
            lock (m_Lock_WorkItem)
            {
                m_WorkQueue.AddPrioritised(oPThreadWorkItem);
            }

            if (m_ItemsProcessing < m_NoParallelThreads) //Spawn another thread.
                SpawnWorkThread(null);
        }

        /// <summary>
        /// This is the method is used from both <see cref="QueueUserWorkItem"/> and the <see cref="AsyncCallback"/>.
        /// 
        /// If from the QueueUserWorkItem(), then the parameter is null, and it is not nessecary to call EndInvoke.
        /// 
        /// If it is from a AsyncCallback then it is nessecary to call end invoke, tidy up the manual resetevents
        /// 
        /// Both code paths then spawn another workitem asycronusly on the threadpool
        /// </summary>
        /// <param name="AsyncResult">IAsyncResult from the previous Async invokation on the threadpool, or null if
        /// from  <see cref="QueueUserWorkItem"/></param>
        private void SpawnWorkThread(IAsyncResult AsyncResult)
        {
            lock (m_Lock_WorkItem)
            {
                if (AsyncResult != null)
                {
                    oWaitCallback.EndInvoke(AsyncResult);

                    m_ItemsProcessing--;

                    #region Removes the ManualReset from the collection, indicating it is processed.
                    WorkItem oPThreadWorkItem = AsyncResult.AsyncState as WorkItem;

                    if (oPThreadWorkItem != null)
                    {
                        m_WorkQueue.ManualResets.Remove(oPThreadWorkItem.MRE);
                    }
                    #endregion

                    //if (ThreadQueueMonitor.Instance.ThreadPoolThreadCount != null)
                    //    ThreadQueueMonitor.Instance.ThreadPoolThreadCount.Decrement();
                }

                // oWorker item will be null if the queue was empty
                if (m_WorkQueue.Count > 0)
                {
                    m_ItemsProcessing++;

                    //Gets the next piece of work to perform
                    WorkItem oWorkItem = m_WorkQueue.Dequeue();

                    if (oWorkItem != null)
                    {
                        //Hooks up the callback to this method.		
                        AsyncCallback oAsyncCallback = new AsyncCallback(this.SpawnWorkThread);

                        //Invokes the work on the threadpool					
                        oWaitCallback.BeginInvoke(oWorkItem, oAsyncCallback, oWorkItem);

                        //if (ThreadQueueMonitor.Instance.ThreadPoolThreadCount != null)
                        //    ThreadQueueMonitor.Instance.ThreadPoolThreadCount.Increment();
                    }
                }

                //Debug.WriteLine(String.Format("SpawnWorkThread Length:{0} InUse:{1} DateTime:{2}", m_WorkQueue.Count, m_ItemsProcessing, DateTime.Now.ToString()));
            }
        }

        /// <summary>
        /// Can only be called by <see cref="SpawnWorkThread"/> it is where the pointer to the method 
        /// is invoked, and the parameter is passed to the method.
        /// 
        /// Importantly, this is where reset event is set, to indicate that work has been completed.
        /// </summary>
        /// <param name="Item"></param>
        private void InvokeWaitHandleDelegate(object Item)
        {
            try
            {
                WorkItem oWorkItem = Item as WorkItem;

                try
                {
                    oWorkItem.Method.Invoke(oWorkItem.State);
                }
                catch (Exception Ex)
                {
                    Debug.WriteLine("InvokeWaitHandleDelegate.Ex.2:" + Ex.ToString());
                }
                finally
                {
                    oWorkItem.MRE.Set(); //ManualResetEvent is set, in order to flag it is completed.
                }
            }
            catch (Exception Ex)
            {
                Debug.WriteLine("InvokeWaitHandleDelegate.Ex.1:" + Ex.ToString());
            }
        }


        /// <summary>
        /// This causes the work queue to block, until all the currently queued workitems have been processed. 
        /// It takes a snapshot of the pending items, and waits for them to process.
        /// 
        /// This code will wait for all pending items to be processed, then it will unblock
        ///         
        /// </summary>
        /// <returns>The number of remaining worker items</returns>
        public int WaitAll()
        {
            lock (this)
            {
                //This collection is changed by many threads, it must therefore be copied before it is enumerated.
                List<ManualResetEvent> oCopy = new List<ManualResetEvent>(m_WorkQueue.ManualResets);

                foreach (ManualResetEvent oMR in oCopy)
                    oMR.WaitOne();

                return m_WorkQueue.ManualResets.Count;
            }
        }

        public WorkItemQueue WorkQueue
        {
            get { return m_WorkQueue; }
        }
    }

    class WorkItem : IComparable<WorkItem>
    {
        private WaitCallback m_Method;
        private Object m_ObjectState;
        private ThreadPriority m_Priority;
        private ManualResetEvent m_MRE;
        private object m_ItemKey;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="method">The method that will be invoked on the threadpool</param>
        /// <param name="state">The object that will be passed as a parameter to your method</param>
        /// <param name="priority">Thread Priority, used to order the pending workitems</param>
        /// <param name="ItemKey">Key to represent the uniqueness of this workitem, used to reduce the amount of redunent work on the workerqueue</param>
        public WorkItem(WaitCallback method, Object state, ThreadPriority priority, object ItemKey)
        {
            m_Method = method;
            m_ObjectState = state;
            m_Priority = priority;
            m_ItemKey = ItemKey;

            m_MRE = new ManualResetEvent(false);
        }

        public ThreadPriority Priority { get { return m_Priority; } }
        public WaitCallback Method { get { return m_Method; } }
        public Object State { get { return m_ObjectState; } }
        public Object Key { get { return m_ItemKey; } }
        public ManualResetEvent MRE { get { return m_MRE; } }

        #region IComparable<PThreadWorkItem> Members

        public int CompareTo(WorkItem other)
        {
            return this.Priority.CompareTo(other.Priority);
        }

        #endregion
    }

    class WorkItemQueue
    {
        private object m_Lock_WorkItem_AddRemove;

        private List<WorkItem> m_WorkItems;
        private List<ManualResetEvent> m_MREs;

        public WorkItemQueue()
        {
            m_Lock_WorkItem_AddRemove = new object();

            m_WorkItems = new List<WorkItem>();
            m_MREs = new List<ManualResetEvent>();
        }

        /// <summary>
        /// Removes the next highest priority work item from the queue.
        /// </summary>
        /// <returns></returns>
        public WorkItem Dequeue()
        {
            lock (m_Lock_WorkItem_AddRemove)
            {
                if (m_WorkItems.Count > 0)
                {
                    WorkItem o = m_WorkItems[m_WorkItems.Count - 1];
                    m_WorkItems.RemoveAt(m_WorkItems.Count - 1);
                    return o;
                }

                return null;
            }
        }

        /// <summary>
        /// Adds items to the work queue.
        /// 
        /// If the unique for the item already exists, then the work item is discarded.
        /// 
        /// If the priority is greater than previous items in the queue, then the new work item is
        /// inserted prior to the other items.
        /// 
        /// This code maintains the sorted order of the workitems.
        /// </summary>
        /// <param name="oNewItem"></param>
        public void AddPrioritised(WorkItem oNewItem)
        {
            lock (m_Lock_WorkItem_AddRemove)
            {
                foreach (WorkItem oItem in m_WorkItems)
                    if (oItem.Key.ToString() == oNewItem.Key.ToString())
                    {
                        Debug.WriteLine("PrioritisedArrayList: Key Already Queued: Skipped Request");
                        return;
                    }

                m_MREs.Add(oNewItem.MRE);

                int i = m_WorkItems.BinarySearch(oNewItem);

                if (i < 0)
                    m_WorkItems.Insert(~i, oNewItem);
                else
                    m_WorkItems.Insert(i, oNewItem);
            }
        }

        public List<ManualResetEvent> ManualResets
        {
            get { return m_MREs; }
        }

        public int Count
        {
            get { return m_WorkItems.Count; }
        }
    }

    
}
