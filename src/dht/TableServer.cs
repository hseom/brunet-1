/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2007 University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using System.Text;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Collections.Generic;

#if BRUNET_NUNIT
using NUnit.Framework;
using System.Threading;
#endif

using Brunet;

namespace Brunet.Dht {
  public class TableServer : IRpcHandler {
    protected object _sync, _transfer_sync;

    /* Why on earth does the SortedList only allow sorting based upon keys?
     * I should really implement a more general SortedList, but we want this 
     * working asap...
     */
    private TableServerData _data;
    private Node _node;
    protected Address _right_addr = null, _left_addr = null;
    protected TransferState _right_transfer_state = null, _left_transfer_state = null;
    protected bool _dhtactivated = false;
    public bool Activated { get { return _dhtactivated; } }
    public bool debug = false;
    private RpcManager _rpc;

    public TableServer(Node node, RpcManager rpc) {
      _sync = new object();
      _node = node;
      _rpc = rpc;
      _data = new TableServerData(_node);
      _transfer_sync = new object();
      lock(_transfer_sync) {
        node.ConnectionTable.ConnectionEvent += this.ConnectionHandler;
        node.ConnectionTable.DisconnectionEvent += this.ConnectionHandler;
        node.ConnectionTable.StatusChangedEvent += this.StatusChangedHandler;
      }
    }

    /* This is very broken now, we will need to manually update count for it
    * to work properly
    */
    public int GetCount() {
      lock(_sync) {
        return _data.GetCount();
      }
    }


//  We implement IRpcHandler to help with Puts, so we must have this method to process
//  new Rpc commands on this object
    public void HandleRpc(ISender caller, string method, IList args, object rs) {
      // We have special case for the puts since they are done asynchronously
      if(method == "Put") {
        try {
          MemBlock key = (byte[]) args[0];
          MemBlock value = (byte[]) args[1];
          int ttl = (int) args[2];
          bool unique = (bool) args[3];
          Put(key, value, ttl, unique, rs);
        }
        catch (Exception e) {
          object result = new AdrException(-32602, e);
          _rpc.SendResult(rs, result);
        }
      }
      else {
        // Everybody else just uses the generic synchronous style
        object result = null;
        try {
          Type type = this.GetType();
          MethodInfo mi = type.GetMethod(method);
          object[] arg_array = new object[ args.Count ];
          args.CopyTo(arg_array, 0);
          result = mi.Invoke(this, arg_array);
        }
        catch(Exception e) {
          result = new AdrException(-32602, e);
        }
        _rpc.SendResult(rs, result);
      }
    }

    /**
     * This method is called by a Dht client to place data into the Dht
     * @param key key associated with the date item
     * @param key key associated with the date item
     * @param data data associated with the key
     * @param ttl time-to-live in seconds
     * @param unique determines whether or not this is a put or a create
     * @return true on success, thrown exception on failure
    */

    /* First we try locally and then remotely, they should both except if 
     * failure, so if we get rv == true + exception, we were successful
     * but remote wasn't, so we remove locally
     */

        // Here we receive the results of our put follow ups, for simplicity, we
        // have both the local Put and the remote PutHandler return the results
        // via the blockingqueue.  If it fails, we remove it locally, if the item
        // was never created it shouldn't matter.


    public void Put(MemBlock key, MemBlock value, int ttl, bool unique, object rs) {
      object result = null;
      try {
        PutHandler(key, value, ttl, unique);
        BlockingQueue remote_put = new BlockingQueue();
        remote_put.EnqueueEvent += delegate(Object o, EventArgs eargs) {
          result = null;
          try {
            bool timedout;
            result = remote_put.Dequeue(0, out timedout);
            RpcResult rpcResult = (RpcResult) result;
            result = rpcResult.Result;
            if(result.GetType() != typeof(bool)) {
              throw new Exception("Incompatible return value.");
            }
          }
          catch (Exception e) {
            result = new AdrException(-32602, e);
            _data.RemoveEntry(key, value);
          }

          remote_put.Close();
          _rpc.SendResult(rs, result);
        };


        Address key_address = new AHAddress(key);
        ISender s = null;
        // We need to forward this to the appropriate node!
        if(((AHAddress)_node.Address).IsLeftOf((AHAddress) key_address)) {
          Connection con = _node.ConnectionTable.GetRightStructuredNeighborOf((AHAddress) _node.Address);
          s = con.Edge;
        }
        else {
          Connection con = _node.ConnectionTable.GetLeftStructuredNeighborOf((AHAddress) _node.Address);
          s = con.Edge;
        }
        _rpc.Invoke(s, remote_put, "dht.PutHandler", key, value, ttl, unique);
      }
      catch (Exception e) {
        result = new AdrException(-32602, e);
        _rpc.SendResult(rs, result);
      }
    }

    /**
     * This method puts in a key-value pair at this node
     * @param key key associated with the date item
     * @param data data associated with the key
     * @param ttl time-to-live in seconds
     * @return true on success, thrown exception on failure
     */

    public bool PutHandler(byte[] keyb, byte[] valueb, int ttl, bool unique) {
      MemBlock key = MemBlock.Reference(keyb);
      MemBlock value = MemBlock.Reference(valueb);

      DateTime create_time = DateTime.UtcNow;
      TimeSpan ts = new TimeSpan(0,0,ttl);
      DateTime end_time = create_time + ts;

      lock(_sync) {
        _data.DeleteExpired(key);
        ArrayList data = _data.GetEntries(key);
        if(data != null) {
          foreach(Entry ent in data) {
            if(ent.Value.Equals(value)) {
              if(end_time > ent.EndTime) {
                _data.UpdateEntry(ent.Key, ent.Value, end_time);
              }
              return true;
            }
          }
          // If this is a create we didn't find an previous entry, so failure, else add it
          if(unique) {
            throw new Exception("ENTRY_ALREADY_EXISTS");
          }
        }
        else {
          //This is a new key
          data = new ArrayList();
        }

        // This is either a new key or a new value (put only)
        Entry e = new Entry(key, value, create_time, end_time);
        _data.AddEntry(e);
      } // end of lock
      return true;
    }

    /**
    * Retrieves data from the Dht
    * @param key key associated with the date item
    * @param maxbytes amount of data to retrieve
    * @param token an array of ints used for continuing gets
    * @return IList of results
    */

    public IList Get(byte[] keyb, int maxbytes, byte[] token) {
      MemBlock key = MemBlock.Reference(keyb);
      int seen_start_idx = 0;
      int seen_end_idx = 0;
      if( token != null ) {
        int[] bounds = (int[])AdrConverter.Deserialize(new System.IO.MemoryStream(token));
        seen_start_idx = bounds[0];
        seen_end_idx = bounds[1];
        seen_start_idx = seen_end_idx + 1;
      }

      int consumed_bytes = 0;

      ArrayList result = new ArrayList();
      ArrayList values = new ArrayList();
      int remaining_items = 0;
      byte[] next_token = null;

      lock(_sync ) {
        _data.DeleteExpired(key);
        ArrayList data = _data.GetEntries(key);

        // Keys exist!
        if( data != null ) {
          seen_end_idx = data.Count - 1;
          for(int i = seen_start_idx; i < data.Count; i++) {
            Entry e = (Entry) data[i];
            if (e.Value.Length + consumed_bytes <= maxbytes) {
              int age = (int) (DateTime.UtcNow - e.CreateTime).TotalSeconds;
              int ttl = (int) (e.EndTime - e.CreateTime).TotalSeconds;
              consumed_bytes += e.Value.Length;
              Hashtable item = new Hashtable();
              item["age"] = age;
              item["value"] = (byte[])e.Value;
              item["ttl"] = ttl;
              values.Add(item);
            }
            else {
              seen_end_idx = i - 1;
              break;
            }
          }
          remaining_items = data.Count - (seen_end_idx + 1);
        }
      }//End of lock
      //we have added new item: update the token
      int[] new_bounds = new int[2];
      new_bounds[0] = seen_start_idx;
      new_bounds[1] = seen_end_idx;
      //new_bounds has to be converted to a new token
      System.IO.MemoryStream ms = new System.IO.MemoryStream();
      AdrConverter.Serialize(new_bounds, ms);
      next_token = ms.ToArray();
      result.Add(values);
      result.Add(remaining_items);
      result.Add(next_token);
      return result;
    }

    /** protected methods. */

    /* This method checks to see if the node is connected and activates
     * the Dht if it is.
     */
    protected void StatusChangedHandler(object contab, EventArgs eargs) {
      if(!_dhtactivated && _node.IsConnected) {
            _dhtactivated = true;
      }
    }

    /* This is called whenever there is a disconnect or a connect, the idea
     * is to determine if there is a new left or right node, if there is and
     * there is a pre-existing transfer, we must interuppt it, and start a new
     * transfer
     */

    private void ConnectionHandler(object o, EventArgs eargs) {
      ConnectionEventArgs cargs = eargs as ConnectionEventArgs;
      Connection old_con = cargs.Connection;
      //first make sure that it is a new StructuredConnection
      if (old_con.MainType != ConnectionType.Structured) {
        return;
      }
      lock(_transfer_sync) {
        ConnectionTable tab = _node.ConnectionTable;
        Connection lc = null, rc = null;
        try {
          lc = tab.GetLeftStructuredNeighborOf((AHAddress) _node.Address);
        }
        catch(Exception) {}
        try {
          rc = tab.GetRightStructuredNeighborOf((AHAddress) _node.Address);
        }
        catch(Exception) {}

        /* Cases
         * no change on left
         * new left node with no previous node (from disc or new node)
         * left disconnect and new left ready
         * left disconnect and no one ready
         * no change on right
         * new right node with no previous node (from disc or new node)
         * right disconnect and new right ready
         * right disconnect and no one ready
         */
        if(lc != null) {
          if(lc.Address != _left_addr) {
            if(_left_transfer_state != null) {
              _left_transfer_state.Interrupt();
              _left_transfer_state = null;
            }
            _left_addr = lc.Address;
            _left_transfer_state = new TransferState(true, this);
          }
        }
        else if(_left_addr != null) {
          if(_left_transfer_state != null) {
            _left_transfer_state.Interrupt();
            _left_transfer_state = null;
          }
          _left_addr = null;
        }

        if(rc != null) {
          if(rc.Address != _right_addr) {
            if(_right_transfer_state != null) {
              _right_transfer_state.Interrupt();
              _right_transfer_state = null;
            }
            _right_addr = rc.Address;
            _right_transfer_state = new TransferState(false, this);
          }
        }
        else if(_right_addr != null) {
          if(_right_transfer_state != null) {
            _right_transfer_state.Interrupt();
            _right_transfer_state = null;
          }
          _right_addr = null;
        }
      }
    }

    protected class TransferState {
      protected const int MAX_PARALLEL_TRANSFERS = 1;
      private object remaining = MAX_PARALLEL_TRANSFERS;
      bool left, interrupted, complete;
      MemBlock current_key;
      Connection _con;
      List<MemBlock> completed_keys = new List<MemBlock>();
      List<MemBlock> completed_values = new List<MemBlock>();
      TableServer _ts;

      public TransferState(bool left, TableServer ts) {
        this._ts = ts;
        this.interrupted = false;
        this.complete = false;
        this.left = left;
        ConnectionTable tab = _ts._node.ConnectionTable;
        if(left) {
          try {
            _con = tab.GetLeftStructuredNeighborOf((AHAddress) _ts._node.Address);
          }
          catch(Exception) {}
        }
        else {
          try {
            _con = tab.GetRightStructuredNeighborOf((AHAddress) _ts._node.Address);
          }
          catch(Exception) {}
        }
        if(_con == null) {
          return;
        }
        int count = 0;
        foreach(MemBlock key in _ts._data.GetKeys()) {
          bool left_of_node = ((AHAddress)_ts._node.Address).IsRightOf(new AHAddress(key));
          if(left && left_of_node) {
            current_key = key;
            break;
          }
          else if(!left && !left_of_node) {
            current_key = key;
            break;
          }

          ArrayList data = _ts._data.GetEntries(key);
          for(int i = 0; i < data.Count; i++) {
            if(interrupted) {
              count = MAX_PARALLEL_TRANSFERS;
              break;
            }
            Entry ent = (Entry) data[i];
            BlockingQueue queue = new BlockingQueue();
            queue.EnqueueEvent += this.NextTransfer;
            queue.CloseEvent += this.NextTransfer;
            int ttl = (int) (ent.EndTime - DateTime.UtcNow).TotalSeconds;
            _ts._rpc.Invoke(_con.Edge, queue, "dht.PutHandler", key, ent.Value, ttl, false);
            if(_ts.debug) {
              Console.WriteLine(_ts._node.Address + " transferring " + new BigInteger(key) + ":" + i + " to " + _con.Address + ".");
            }
            /* Time to check if we also need to update our neighbor's neighbor,
             * this probably isn't required except for rare cases where two
             * nodes come up at the same time in the same region and become
             * neighbors.  But if we don't do this, we would lose half of a
             * value.
             */
     /*       if(left && ((AHAddress)_left_addr).IsRightOf(new AHAddress(key))) {
              _ts._rpc.Invoke(_con.Edge, queue, "dht.PutHandler", key, ent.Value, ttl, false);
            }
            else if(!left && ((AHAddress)_right_addr).IsLeftOf(new AHAddress(key))) {
            }*/
            completed_values.Add(ent.Value);
            if(i == data.Count - 1) {
              completed_values.Clear();
              completed_keys.Add(key);
              current_key = null;
            }
            if(++count == MAX_PARALLEL_TRANSFERS) {
              break;
            }
          }
          if(count == MAX_PARALLEL_TRANSFERS) {
            break;
          }
        }
      }

      private void NextTransfer(Object o, EventArgs eargs) {
        BlockingQueue queue = (BlockingQueue) o;
        queue.EnqueueEvent -= this.NextTransfer;
        queue.CloseEvent -= this.NextTransfer;
        if(interrupted) {
          return;
        }
        if(!complete) {
          foreach(MemBlock key in _ts._data.GetKeys()) {
            if(current_key == null) {
              bool left_of_node = ((AHAddress)_ts._node.Address).IsRightOf(new AHAddress(key));
              if(left && left_of_node && !completed_keys.Contains(key)) {
                current_key = key;
                break;
              }
              else if(!left && !left_of_node && !completed_keys.Contains(key)) {
                current_key = key;
                break;
              }
            }
          }

          if(current_key == null) {
            complete = true;
          }
          else {
            ArrayList data = _ts._data.GetEntries(current_key);
            for(int i = 0; i < data.Count; i++) {
              Entry ent = (Entry) data[i];
              if(completed_values.Contains(ent.Value)) {
                continue;
              }
              queue = new BlockingQueue();
              queue.EnqueueEvent += this.NextTransfer;
              queue.CloseEvent += this.NextTransfer;
              int ttl = (int) (ent.EndTime - DateTime.UtcNow).TotalSeconds;
              _ts._rpc.Invoke(_con.Edge, queue, "dht.PutHandler", current_key, ent.Value, ttl, false);
              if(_ts.debug) {
                Console.WriteLine(_ts._node.Address + " transferring " + new BigInteger(current_key) + ":" + i + " to " + _con.Address + ".");
              }
              completed_values.Add(ent.Value);
              if(i == data.Count - 1) {
                completed_values.Clear();
                completed_keys.Add(current_key);
                current_key = null;
              }
            }
          }
        }
        if (complete) {
          lock(remaining) {
            remaining = (int) remaining - 1;
            if((int) remaining == 0) {
/*              foreach(MemBlock key in completed_keys) {
                if(left && ((AHAddress)_ts._left_addr).IsLeftOf(new AHAddress(key))) {
                  _ts._data.RemoveEntries(key);
                }
                else if(!left &&((AHAddress)_ts._right_addr).IsRightOf(new AHAddress(key))) {
                  _ts._data.RemoveEntries(key);
                }
              }*/
              if(_ts.debug) {
                Console.WriteLine(_ts._node.Address + " completed transfer  to " + _con.Address + ".");
              }
            }
          }
        }
      }

      public void Interrupt() {
        if(_ts.debug) {
          Console.WriteLine(_ts._node.Address + " interrupted during transfer  to " + _con.Address + ".");
        }
        interrupted = true;
        complete = true;
        completed_keys.Clear();
        completed_values.Clear();
        current_key = null;
      }
    }
  }
}
